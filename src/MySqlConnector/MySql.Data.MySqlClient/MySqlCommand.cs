using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector.Core;
using MySqlConnector.Protocol;
using MySqlConnector.Protocol.Payloads;
using MySqlConnector.Protocol.Serialization;
using MySqlConnector.Utilities;

namespace MySql.Data.MySqlClient
{
	public sealed class MySqlCommand : DbCommand
	{
		public MySqlCommand()
			: this(null, null, null)
		{
		}

		public MySqlCommand(string commandText)
			: this(commandText, null, null)
		{
		}

		public MySqlCommand(MySqlConnection connection, MySqlTransaction transaction)
			: this(null, connection, transaction)
		{
		}

		public MySqlCommand(string commandText, MySqlConnection connection)
			: this(commandText, connection, null)
		{
		}

		public MySqlCommand(string commandText, MySqlConnection connection, MySqlTransaction transaction)
		{
			CommandId = Interlocked.Increment(ref s_commandId);
			CommandText = commandText;
			Connection = connection;
			Transaction = transaction;
			m_parameterCollection = new MySqlParameterCollection();
			CommandType = CommandType.Text;
		}

		public new MySqlParameterCollection Parameters
		{
			get
			{
				VerifyNotDisposed();
				return m_parameterCollection;
			}
		}

		public new MySqlParameter CreateParameter() => (MySqlParameter) base.CreateParameter();

		public override void Cancel() => Connection.Cancel(this);

		public override int ExecuteNonQuery() => ExecuteNonQueryAsync(IOBehavior.Synchronous, CancellationToken.None).GetAwaiter().GetResult();

		public override object ExecuteScalar() => ExecuteScalarAsync(IOBehavior.Synchronous, CancellationToken.None).GetAwaiter().GetResult();

		public new MySqlDataReader ExecuteReader() => (MySqlDataReader) base.ExecuteReader();

		public new MySqlDataReader ExecuteReader(CommandBehavior commandBehavior) => (MySqlDataReader) base.ExecuteReader(commandBehavior);

		public override void Prepare() => PrepareAsync(IOBehavior.Synchronous, default).GetAwaiter().GetResult();
		public Task PrepareAsync() => PrepareAsync(AsyncIOBehavior, default);
		public Task PrepareAsync(CancellationToken cancellationToken) => PrepareAsync(AsyncIOBehavior, cancellationToken);
		
		private async Task PrepareAsync(IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			if (Connection == null)
				throw new InvalidOperationException("Connection property must be non-null.");
			if (Connection.State != ConnectionState.Open)
				throw new InvalidOperationException("Connection must be Open; current state is {0}".FormatInvariant(Connection.State));
			if (string.IsNullOrWhiteSpace(CommandText))
				throw new InvalidOperationException("CommandText must be specified");
			if (m_connection?.HasActiveReader ?? false)
				throw new InvalidOperationException("Cannot call Prepare when there is an open DataReader for this command; it must be closed first.");
			if (Connection.IgnorePrepare)
				return;

			if (CommandType != CommandType.Text)
				throw new NotSupportedException("Only CommandType.Text is currently supported by MySqlCommand.Prepare");

			var statementPreparer = new StatementPreparer(CommandText, Parameters, CreateStatementPreparerOptions());
			var parsedStatements = statementPreparer.SplitStatements();

			if (parsedStatements.Statements.Count > 1)
				throw new NotSupportedException("Multiple semicolon-delimited SQL statements are not supported by MySqlCommand.Prepare");

			var columnsAndParameters = new ResizableArray<byte>();
			var columnsAndParametersSize = 0;

			var preparedStatements = new List<PreparedStatement>(parsedStatements.Statements.Count);
			foreach (var statement in parsedStatements.Statements)
			{
				await Connection.Session.SendAsync(new PayloadData(statement.StatementBytes), ioBehavior, cancellationToken).ConfigureAwait(false);
				var payload = await Connection.Session.ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
				var response = StatementPrepareResponsePayload.Create(payload);

				ColumnDefinitionPayload[] parameters = null;
				if (response.ParameterCount > 0)
				{
					parameters = new ColumnDefinitionPayload[response.ParameterCount];
					for (var i = 0; i < response.ParameterCount; i++)
					{
						payload = await Connection.Session.ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
						Utility.Resize(ref columnsAndParameters, columnsAndParametersSize + payload.ArraySegment.Count);
						Buffer.BlockCopy(payload.ArraySegment.Array, payload.ArraySegment.Offset, columnsAndParameters.Array, columnsAndParametersSize, payload.ArraySegment.Count);
						parameters[i] = ColumnDefinitionPayload.Create(new ResizableArraySegment<byte>(columnsAndParameters, columnsAndParametersSize, payload.ArraySegment.Count));
						columnsAndParametersSize += payload.ArraySegment.Count;
					}
					if (!Connection.Session.SupportsDeprecateEof)
					{
						payload = await Connection.Session.ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
						EofPayload.Create(payload);
					}
				}

				ColumnDefinitionPayload[] columns = null;
				if (response.ColumnCount > 0)
				{
					columns = new ColumnDefinitionPayload[response.ColumnCount];
					for (var i = 0; i < response.ColumnCount; i++)
					{
						payload = await Connection.Session.ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
						Utility.Resize(ref columnsAndParameters, columnsAndParametersSize + payload.ArraySegment.Count);
						Buffer.BlockCopy(payload.ArraySegment.Array, payload.ArraySegment.Offset, columnsAndParameters.Array, columnsAndParametersSize, payload.ArraySegment.Count);
						columns[i] = ColumnDefinitionPayload.Create(new ResizableArraySegment<byte>(columnsAndParameters, columnsAndParametersSize, payload.ArraySegment.Count));
						columnsAndParametersSize += payload.ArraySegment.Count;
					}
					if (!Connection.Session.SupportsDeprecateEof)
					{
						payload = await Connection.Session.ReceiveReplyAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
						EofPayload.Create(payload);
					}
				}

				preparedStatements.Add(new PreparedStatement(response.StatementId, statement, columns, parameters));
			}

			m_parsedStatements = parsedStatements;
			m_statements = preparedStatements;
		}

		public override string CommandText
		{
			get => m_commandText;
			set
			{
				if (m_connection?.HasActiveReader ?? false)
					throw new InvalidOperationException("Cannot set MySqlCommand.CommandText when there is an open DataReader for this command; it must be closed first.");
				m_commandText = value;
				ClearPreparedStatements();
			}
		}

		public bool IsPrepared => m_statements != null;

		public new MySqlTransaction Transaction { get; set; }

		public new MySqlConnection Connection
		{
			get => m_connection;
			set
			{
				if (m_connection?.HasActiveReader ?? false)
					throw new InvalidOperationException("Cannot set MySqlCommand.Connection when there is an open DataReader for this command; it must be closed first.");
				m_connection = value;
				ClearPreparedStatements();
			}
		}

		public override int CommandTimeout
		{
			get => Math.Min(m_commandTimeout ?? Connection?.DefaultCommandTimeout ?? 0, int.MaxValue / 1000);
			set => m_commandTimeout = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "CommandTimeout must be greater than or equal to zero.");
		}

		public override CommandType CommandType
		{
			get => m_commandType;
			set
			{
				if (value != CommandType.Text && value != CommandType.StoredProcedure)
					throw new ArgumentException("CommandType must be Text or StoredProcedure.", nameof(value));
				m_commandType = value;
				ClearPreparedStatements();
			}
		}

		public override bool DesignTimeVisible { get; set; }

		public override UpdateRowSource UpdatedRowSource { get; set; }

		public long LastInsertedId { get; internal set; }

		protected override DbConnection DbConnection
		{
			get => Connection;
			set => Connection = (MySqlConnection) value;
		}

		protected override DbParameterCollection DbParameterCollection => m_parameterCollection;

		protected override DbTransaction DbTransaction
		{
			get => Transaction;
			set => Transaction = (MySqlTransaction) value;
		}

		protected override DbParameter CreateDbParameter()
		{
			VerifyNotDisposed();
			return new MySqlParameter();
		}

		protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
		{
			ResetCommandTimeout();
			return ExecuteReaderAsync(behavior, IOBehavior.Synchronous, CancellationToken.None).GetAwaiter().GetResult();
		}

		public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
			ExecuteNonQueryAsync(AsyncIOBehavior, cancellationToken);

		internal async Task<int> ExecuteNonQueryAsync(IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			ResetCommandTimeout();
			using (var reader = (MySqlDataReader) await ExecuteReaderAsync(CommandBehavior.Default, ioBehavior, cancellationToken).ConfigureAwait(false))
			{
				do
				{
					while (await reader.ReadAsync(ioBehavior, cancellationToken).ConfigureAwait(false))
					{
					}
				} while (await reader.NextResultAsync(ioBehavior, cancellationToken).ConfigureAwait(false));
				return reader.RecordsAffected;
			}
		}

		public override Task<object> ExecuteScalarAsync(CancellationToken cancellationToken) =>
			ExecuteScalarAsync(AsyncIOBehavior, cancellationToken);

		internal async Task<object> ExecuteScalarAsync(IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			ResetCommandTimeout();
			var hasSetResult = false;
			object result = null;
			using (var reader = (MySqlDataReader) await ExecuteReaderAsync(CommandBehavior.SingleResult | CommandBehavior.SingleRow, ioBehavior, cancellationToken).ConfigureAwait(false))
			{
				do
				{
					var hasResult = await reader.ReadAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
					if (!hasSetResult)
					{
						if (hasResult)
							result = reader.GetValue(0);
						hasSetResult = true;
					}
				} while (await reader.NextResultAsync(ioBehavior, cancellationToken).ConfigureAwait(false));
			}
			return result;
		}

		protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
		{
			ResetCommandTimeout();
			return ExecuteReaderAsync(behavior, AsyncIOBehavior, cancellationToken);
		}

		internal Task<DbDataReader> ExecuteReaderAsync(CommandBehavior behavior, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			if (!IsValid(out var exception))
				return Utility.TaskFromException<DbDataReader>(exception);

			if (m_statements != null)
				m_commandExecutor = new PreparedStatementCommandExecutor(this);
			else if (m_commandType == CommandType.Text)
				m_commandExecutor = new TextCommandExecutor(this);
			else if (m_commandType == CommandType.StoredProcedure)
				m_commandExecutor = new StoredProcedureCommandExecutor(this);

			return m_commandExecutor.ExecuteReaderAsync(CommandText, m_parameterCollection, behavior, ioBehavior, cancellationToken);
		}

		protected override void Dispose(bool disposing)
		{
			try
			{
				if (disposing)
				{
					m_parameterCollection = null;
					ClearPreparedStatements();
				}
			}
			finally
			{
				base.Dispose(disposing);
			}
		}

		/// <summary>
		/// Registers <see cref="Cancel"/> as a callback with <paramref name="token"/> if cancellation is supported.
		/// </summary>
		/// <param name="token">The <see cref="CancellationToken"/>.</param>
		/// <returns>An object that must be disposed to revoke the cancellation registration.</returns>
		/// <remarks>This method is more efficient than calling <code>token.Register(Command.Cancel)</code> because it avoids
		/// unnecessary allocations.</remarks>
		internal IDisposable RegisterCancel(CancellationToken token)
		{
			if (!token.CanBeCanceled)
				return null;

			if (m_cancelAction == null)
				m_cancelAction = Cancel;
			return token.Register(m_cancelAction);
		}

		internal int CommandId { get; }

		internal int CancelAttemptCount { get; set; }

		internal IReadOnlyList<PreparedStatement> PreparedStatements => m_statements;

		/// <summary>
		/// Causes the effective command timeout to be reset back to the value specified by <see cref="CommandTimeout"/>.
		/// </summary>
		/// <remarks>As per the <a href="https://msdn.microsoft.com/en-us/library/system.data.sqlclient.sqlcommand.commandtimeout.aspx">MSDN documentation</a>,
		/// "This property is the cumulative time-out (for all network packets that are read during the invocation of a method) for all network reads during command
		/// execution or processing of the results. A time-out can still occur after the first row is returned, and does not include user processing time, only network
		/// read time. For example, with a 30 second time out, if Read requires two network packets, then it has 30 seconds to read both network packets. If you call
		/// Read again, it will have another 30 seconds to read any data that it requires."
		/// The <see cref="ResetCommandTimeout"/> method is called by public ADO.NET API methods to reset the effective time remaining at the beginning of a new
		/// method call.</remarks>
		internal void ResetCommandTimeout()
		{
			var commandTimeout = CommandTimeout;
			Connection?.Session?.SetTimeout(commandTimeout == 0 ? Constants.InfiniteTimeout : commandTimeout * 1000);
		}

		internal StatementPreparerOptions CreateStatementPreparerOptions()
		{
			var statementPreparerOptions = StatementPreparerOptions.None;
			if (Connection.AllowUserVariables || CommandType == CommandType.StoredProcedure)
				statementPreparerOptions |= StatementPreparerOptions.AllowUserVariables;
			if (Connection.DateTimeKind == DateTimeKind.Utc)
				statementPreparerOptions |= StatementPreparerOptions.DateTimeUtc;
			else if (Connection.DateTimeKind == DateTimeKind.Local)
				statementPreparerOptions |= StatementPreparerOptions.DateTimeLocal;
			if (CommandType == CommandType.StoredProcedure)
				statementPreparerOptions |= StatementPreparerOptions.AllowOutputParameters;

			switch (Connection.GuidFormat)
			{
			case MySqlGuidFormat.Char36:
				statementPreparerOptions |= StatementPreparerOptions.GuidFormatChar36;
				break;
			case MySqlGuidFormat.Char32:
				statementPreparerOptions |= StatementPreparerOptions.GuidFormatChar32;
				break;
			case MySqlGuidFormat.Binary16:
				statementPreparerOptions |= StatementPreparerOptions.GuidFormatBinary16;
				break;
			case MySqlGuidFormat.TimeSwapBinary16:
				statementPreparerOptions |= StatementPreparerOptions.GuidFormatTimeSwapBinary16;
				break;
			case MySqlGuidFormat.LittleEndianBinary16:
				statementPreparerOptions |= StatementPreparerOptions.GuidFormatLittleEndianBinary16;
				break;
			}

			return statementPreparerOptions;
		}

		private IOBehavior AsyncIOBehavior => Connection?.AsyncIOBehavior ?? IOBehavior.Asynchronous;

		private void VerifyNotDisposed()
		{
			if (m_parameterCollection == null)
				throw new ObjectDisposedException(GetType().Name);
		}

		private bool IsValid(out Exception exception)
		{
			exception = null;
			if (m_parameterCollection == null)
				exception = new ObjectDisposedException(GetType().Name);
			else if (Connection == null)
				exception = new InvalidOperationException("Connection property must be non-null.");
			else if (Connection.State != ConnectionState.Open && Connection.State != ConnectionState.Connecting)
				exception = new InvalidOperationException("Connection must be Open; current state is {0}".FormatInvariant(Connection.State));
			else if (!Connection.IgnoreCommandTransaction && Transaction != Connection.CurrentTransaction)
				exception = new InvalidOperationException("The transaction associated with this command is not the connection's active transaction; see https://github.com/mysql-net/MySqlConnector/issues/474");
			else if (string.IsNullOrWhiteSpace(CommandText))
				exception = new InvalidOperationException("CommandText must be specified");
			return exception == null;
		}

		private void ClearPreparedStatements()
		{
			m_parsedStatements?.Dispose();
			m_parsedStatements = null;
			m_statements = null;
		}

		internal void ReaderClosed() => (m_commandExecutor as StoredProcedureCommandExecutor)?.SetParams();

		static int s_commandId = 1;

		MySqlConnection m_connection;
		string m_commandText;
		MySqlParameterCollection m_parameterCollection;
		ParsedStatements m_parsedStatements;
		IReadOnlyList<PreparedStatement> m_statements;
		int? m_commandTimeout;
		CommandType m_commandType;
		ICommandExecutor m_commandExecutor;
		Action m_cancelAction;
	}
}
