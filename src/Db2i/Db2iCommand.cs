using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using Db2i.Protocol;

namespace Db2i;

/// <summary>Represents an SQL statement to execute on IBM i.</summary>
public sealed class Db2iCommand : DbCommand
{
    private readonly Db2iParameterCollection _parameters = new();
    private readonly object _syncRoot = new();
    private Db2iConnection? _connection;
    private Db2iTransaction? _transaction;
    private Db2iPreparedStatement? _preparedStatement;
    private Db2iDataReader? _activeReader;
    private CancellationTokenSource? _activeOperationCancellation;
    private string _commandText = string.Empty;
    private int _commandTimeout = 30;
    private CommandType _commandType = CommandType.Text;
    private bool _disposed;

    public Db2iCommand()
    {
    }

    public Db2iCommand(string commandText, Db2iConnection connection)
    {
        CommandText = commandText;
        Connection = connection;
    }

    [AllowNull]
    public override string CommandText
    {
        get
        {
            lock (_syncRoot)
            {
                return _commandText;
            }
        }
        set
        {
            var normalized = value ?? string.Empty;
            Db2iPreparedStatement? stale = null;
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (string.Equals(_commandText, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                EnsureNoActiveReader();
                _commandText = normalized;
                stale = _preparedStatement;
                _preparedStatement = null;
            }

            DeleteStatementBestEffort(stale);
        }
    }

    public override int CommandTimeout
    {
        get => _commandTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _commandTimeout = value;
        }
    }

    public override CommandType CommandType
    {
        get => _commandType;
        set => _commandType = value;
    }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.Both;

    public new Db2iConnection? Connection
    {
        get => _connection;
        set
        {
            Db2iPreparedStatement? stale = null;
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (ReferenceEquals(_connection, value))
                {
                    return;
                }

                EnsureNoActiveReader();
                stale = _preparedStatement;
                _preparedStatement = null;
                _connection = value;
            }

            DeleteStatementBestEffort(stale);
        }
    }

    public new Db2iParameterCollection Parameters => _parameters;

    public new Db2iTransaction? Transaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => Connection = value switch
        {
            null => null,
            Db2iConnection connection => connection,
            _ => throw new ArgumentException(
                $"La connessione deve essere di tipo {nameof(Db2iConnection)}.",
                nameof(value)),
        };
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value switch
        {
            null => null,
            Db2iTransaction transaction => transaction,
            _ => throw new ArgumentException(
                $"La transazione deve essere di tipo {nameof(Db2iTransaction)}.",
                nameof(value)),
        };
    }

    public override void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation = _activeOperationCancellation;
        }

        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public override int ExecuteNonQuery()
        => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var (connection, session) = EnsureCanExecute();
        EnsureStatementKind(Db2iStatementKind.NonQuery, nameof(ExecuteNonQuery));
        using var operation = CreateOperationCancellation(cancellationToken);
        Db2iSessionLease? lease = null;
        try
        {
            lease = await session.AcquireOperationAsync(operation.Token).ConfigureAwait(false);
            var statement = await EnsurePreparedAsync(session, operation.Token).ConfigureAwait(false);
            var parameters = await PrepareParametersAsync(
                    session,
                    statement,
                    operation.Token)
                .ConfigureAwait(false);
            return await session.ExecuteNonQueryAsync(
                    statement,
                    parameters,
                    operation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            await HandleCancellationAsync(connection, cancellationToken, operation, exception)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsBrokenStream(exception))
        {
            await connection.AbortCommandAsync().ConfigureAwait(false);
            throw CreateConnectionException(exception);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public override object? ExecuteScalar()
        => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteReaderCoreAsync(
                CommandBehavior.SingleRow | CommandBehavior.SingleResult,
                cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? reader.GetValue(0)
            : null;
    }

    public override void Prepare()
        => PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        var (connection, session) = EnsureCanExecute();
        using var operation = CreateOperationCancellation(cancellationToken);
        Db2iSessionLease? lease = null;
        try
        {
            lease = await session.AcquireOperationAsync(operation.Token).ConfigureAwait(false);
            await EnsurePreparedAsync(session, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            await HandleCancellationAsync(connection, cancellationToken, operation, exception)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsBrokenStream(exception))
        {
            await connection.AbortCommandAsync().ConfigureAwait(false);
            throw CreateConnectionException(exception);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public new Db2iParameter CreateParameter() => new();

    public new Db2iDataReader ExecuteReader()
        => (Db2iDataReader)base.ExecuteReader();

    public new Db2iDataReader ExecuteReader(CommandBehavior behavior)
        => (Db2iDataReader)base.ExecuteReader(behavior);

    protected override DbParameter CreateDbParameter() => new Db2iParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteReaderCoreAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
        => await ExecuteReaderCoreAsync(behavior, cancellationToken).ConfigureAwait(false);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Db2iPreparedStatement? stale = null;
            lock (_syncRoot)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    if (_activeReader is null)
                    {
                        stale = _preparedStatement;
                        _preparedStatement = null;
                    }
                }
            }

            DeleteStatementBestEffort(stale);
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Db2iPreparedStatement? stale = null;
        lock (_syncRoot)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_activeReader is null)
                {
                    stale = _preparedStatement;
                    _preparedStatement = null;
                }
            }
        }

        if (stale is not null)
        {
            await DeleteStatementBestEffortAsync(stale).ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    internal async ValueTask ReaderClosedAsync(
        Db2iDataReader reader,
        CancellationToken cancellationToken,
        bool operationLeaseHeld)
    {
        Db2iPreparedStatement? stale = null;
        lock (_syncRoot)
        {
            if (ReferenceEquals(_activeReader, reader))
            {
                _activeReader = null;
            }

            if (_disposed)
            {
                stale = _preparedStatement;
                _preparedStatement = null;
            }
        }

        if (stale is null)
        {
            return;
        }

        if (operationLeaseHeld)
        {
            await stale.Session.DeleteStatementAsync(stale, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await DeleteStatementBestEffortAsync(stale).ConfigureAwait(false);
        }
    }

    private async Task<Db2iDataReader> ExecuteReaderCoreAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        ValidateBehavior(behavior);
        var (connection, session) = EnsureCanExecute();
        EnsureStatementKind(Db2iStatementKind.Query, nameof(ExecuteReader));
        using var operation = CreateOperationCancellation(cancellationToken);
        Db2iSessionLease? lease = null;
        try
        {
            lease = await session.AcquireOperationAsync(operation.Token).ConfigureAwait(false);
            var statement = await EnsurePreparedAsync(session, operation.Token).ConfigureAwait(false);
            var parameters = await PrepareParametersAsync(
                    session,
                    statement,
                    operation.Token)
                .ConfigureAwait(false);
            var resultFormat = statement.ResultFormat
                ?? throw new InvalidOperationException(
                    "ExecuteReader richiede uno statement che restituisca un result set.");
            var columns = resultFormat.Fields
                .Select(field => field.ToColumn())
                .ToArray();
            Db2iQueryCursor? cursor = null;
            if ((behavior & CommandBehavior.SchemaOnly) == 0)
            {
                cursor = await session.OpenCursorAsync(
                        statement,
                        parameters,
                        (behavior & CommandBehavior.SingleRow) != 0,
                        operation.Token)
                    .ConfigureAwait(false);
            }

            var reader = new Db2iDataReader(
                this,
                connection,
                columns,
                cursor,
                lease,
                behavior,
                CommandTimeout);
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_activeReader is not null)
                {
                    throw new InvalidOperationException(
                        "Il comando ha già un data reader attivo.");
                }

                _activeReader = reader;
            }

            lease = null;
            return reader;
        }
        catch (OperationCanceledException exception)
        {
            await HandleCancellationAsync(connection, cancellationToken, operation, exception)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsBrokenStream(exception))
        {
            await connection.AbortCommandAsync().ConfigureAwait(false);
            throw CreateConnectionException(exception);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async ValueTask<Db2iPreparedStatement> EnsurePreparedAsync(
        Db2iSession session,
        CancellationToken cancellationToken)
    {
        Db2iPreparedStatement? existing;
        lock (_syncRoot)
        {
            existing = _preparedStatement;
            if (existing is not null
                && ReferenceEquals(existing.Session, session)
                && string.Equals(existing.CommandText, _commandText, StringComparison.Ordinal)
                && !existing.IsDeleted)
            {
                return existing;
            }

            _preparedStatement = null;
        }

        if (existing is not null
            && ReferenceEquals(existing.Session, session)
            && !existing.IsDeleted)
        {
            await session.DeleteStatementAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        var prepared = await session.PrepareAsync(
                _commandText,
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _preparedStatement = prepared;
        }

        return prepared;
    }

    private async ValueTask<Db2iParameter[]> PrepareParametersAsync(
        Db2iSession session,
        Db2iPreparedStatement statement,
        CancellationToken cancellationToken)
    {
        var parameters = _parameters.Cast<Db2iParameter>().ToArray();
        var serverParameterCount = statement.ParameterFormat?.Fields.Count ?? 0;
        if (serverParameterCount != parameters.Length)
        {
            throw new InvalidOperationException(
                $"Il comando contiene {serverParameterCount} marker posizionali, " +
                $"ma sono stati forniti {parameters.Length} parametri.");
        }

        if (parameters.Length > 0)
        {
            var parameterFormat = SqlProtocol.ResolveParameterFormat(
                statement.ParameterFormat,
                parameters,
                session.ServerInfo.Ccsid);
            await session.ChangeParameterDescriptorAsync(
                    statement,
                    parameterFormat,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return parameters;
    }

    private (Db2iConnection Connection, Db2iSession Session) EnsureCanExecute()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeReader is not null)
            {
                throw new InvalidOperationException(
                    "Il comando ha già un data reader attivo.");
            }
        }

        if (_connection is null)
        {
            throw new InvalidOperationException("Il comando non ha una connessione associata.");
        }

        if (_commandType != CommandType.Text)
        {
            throw new NotSupportedException(
                "M3 supporta soltanto CommandType.Text.");
        }

        _connection.EnsureOpen();
        if (string.IsNullOrWhiteSpace(_commandText))
        {
            throw new InvalidOperationException("CommandText non può essere vuoto.");
        }

        var statementKind = SqlProtocol.ClassifyStatement(_commandText);
        if (statementKind == Db2iStatementKind.Procedure)
        {
            throw new NotSupportedException(
                "M3 non supporta CALL o le stored procedure.");
        }

        if (statementKind == Db2iStatementKind.TransactionControl)
        {
            throw new NotSupportedException(
                "COMMIT, ROLLBACK e SET TRANSACTION devono essere eseguiti tramite Db2iTransaction.");
        }

        _connection.ValidateCommandTransaction(_transaction);
        return (_connection, _connection.GetOpenSession());
    }

    private void EnsureStatementKind(Db2iStatementKind expected, string operation)
    {
        var actual = SqlProtocol.ClassifyStatement(_commandText);
        if (actual == Db2iStatementKind.Procedure)
        {
            throw new NotSupportedException(
                "M3 non supporta CALL o le stored procedure.");
        }

        if (actual == Db2iStatementKind.TransactionControl)
        {
            throw new NotSupportedException(
                "COMMIT, ROLLBACK e SET TRANSACTION devono essere eseguiti tramite Db2iTransaction.");
        }

        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{operation} non è valido per questo tipo di statement SQL.");
        }
    }

    private static void ValidateBehavior(CommandBehavior behavior)
    {
        const CommandBehavior supported =
            CommandBehavior.Default
            | CommandBehavior.SingleResult
            | CommandBehavior.SchemaOnly
            | CommandBehavior.SingleRow
            | CommandBehavior.SequentialAccess
            | CommandBehavior.CloseConnection;
        if ((behavior & ~supported) != 0)
        {
            throw new NotSupportedException(
                $"CommandBehavior '{behavior & ~supported}' non è supportato da M3.");
        }
    }

    internal OperationCancellation CreateOperationCancellation(CancellationToken callerToken)
        => new(this, callerToken, CommandTimeout);

    internal static async Task HandleCancellationAsync(
        Db2iConnection connection,
        CancellationToken callerToken,
        OperationCancellation operation,
        OperationCanceledException exception)
    {
        var synchronized = await connection.CancelPendingOperationAsync().ConfigureAwait(false);
        if (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                synchronized
                    ? "L'operazione SQL è stata annullata dal chiamante."
                    : "L'operazione SQL è stata annullata e la connessione non ha potuto essere risincronizzata.",
                exception,
                callerToken);
        }

        if (operation.TimedOut)
        {
            throw new Db2iException(
                synchronized
                    ? "Timeout durante l'operazione SQL su IBM i; il comando è stato annullato."
                    : "Timeout durante l'operazione SQL su IBM i; la connessione è stata chiusa.",
                Db2iErrorKind.Timeout,
                exception,
                isTransient: true);
        }

        if (operation.ManuallyCanceled)
        {
            throw new OperationCanceledException(
                synchronized
                    ? "L'operazione SQL è stata annullata tramite Cancel."
                    : "L'operazione SQL è stata annullata e la connessione non ha potuto essere risincronizzata.",
                exception);
        }
    }

    private static bool IsBrokenStream(Exception exception)
        => exception is IOException or SocketException or ObjectDisposedException;

    private static Db2iException CreateConnectionException(Exception exception)
        => new(
            "La connessione IBM i si è interrotta durante l'operazione SQL.",
            Db2iErrorKind.Connection,
            exception,
            isTransient: true);

    private void EnsureNoActiveReader()
    {
        if (_activeReader is not null)
        {
            throw new InvalidOperationException(
                "Il comando non può essere modificato mentre un data reader è attivo.");
        }
    }

    private static void DeleteStatementBestEffort(Db2iPreparedStatement? statement)
    {
        if (statement is null || statement.IsDeleted)
        {
            return;
        }

        try
        {
            using var lease = statement.Session.AcquireOperation();
            statement.Session.DeleteStatementAsync(statement, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
            when (exception is InvalidOperationException
                or ObjectDisposedException
                or IOException
                or SocketException)
        {
            statement.MarkDeleted();
        }
    }

    private static async ValueTask DeleteStatementBestEffortAsync(
        Db2iPreparedStatement statement)
    {
        try
        {
            await using var lease = await statement.Session
                .AcquireOperationAsync(CancellationToken.None)
                .ConfigureAwait(false);
            await statement.Session.DeleteStatementAsync(statement, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException
                or ObjectDisposedException
                or IOException
                or SocketException)
        {
            statement.MarkDeleted();
        }
    }

    internal sealed class OperationCancellation : IDisposable
    {
        private readonly Db2iCommand _owner;
        private readonly CancellationTokenSource _manual;
        private readonly CancellationTokenSource? _timeout;
        private readonly CancellationTokenSource _linked;

        internal OperationCancellation(
            Db2iCommand owner,
            CancellationToken callerToken,
            int timeoutSeconds)
        {
            _owner = owner;
            _manual = owner.RegisterOperationCancellation();
            _timeout = timeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                : null;
            _linked = _timeout is null
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    callerToken,
                    _manual.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    callerToken,
                    _manual.Token,
                    _timeout.Token);
        }

        internal CancellationToken Token => _linked.Token;

        internal bool TimedOut => _timeout?.IsCancellationRequested == true;

        internal bool ManuallyCanceled => _manual.IsCancellationRequested;

        public void Dispose()
        {
            _owner.UnregisterOperationCancellation(_manual);
            _linked.Dispose();
            _timeout?.Dispose();
            _manual.Dispose();
        }
    }

    private CancellationTokenSource RegisterOperationCancellation()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeOperationCancellation is not null)
            {
                throw new InvalidOperationException(
                    "Il comando ha già un'operazione SQL attiva.");
            }

            var cancellation = new CancellationTokenSource();
            _activeOperationCancellation = cancellation;
            return cancellation;
        }
    }

    private void UnregisterOperationCancellation(CancellationTokenSource cancellation)
    {
        lock (_syncRoot)
        {
            if (ReferenceEquals(_activeOperationCancellation, cancellation))
            {
                _activeOperationCancellation = null;
            }
        }
    }
}
