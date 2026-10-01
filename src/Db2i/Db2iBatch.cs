using System.Data;
using System.Data.Common;
using Db2i.Protocol;

namespace Db2i;

/// <summary>
/// Executes SQL statements sequentially on one connection. Use an explicit
/// transaction for atomicity; this batch does not combine network requests.
/// </summary>
public sealed class Db2iBatch : DbBatch
{
    private readonly object _syncRoot = new();
    private readonly Db2iBatchCommandCollection _commands;
    private readonly HashSet<Db2iCommand> _ownedCommands = [];
    private readonly bool _ownsConnection;
    private Db2iConnection? _connection;
    private Db2iTransaction? _transaction;
    private CancellationTokenSource? _cancellation;
    private Db2iBatchDataReader? _reader;
    private Db2iBatchCommand? _currentCommand;
    private Db2iSession? _executionSession;
    private int _timeout = 30;
    private bool _busy;
    private bool _operationActive;
    private bool _disposed;

    public Db2iBatch(Db2iConnection? connection = null, Db2iTransaction? transaction = null)
    {
        _connection = connection;
        _transaction = transaction;
        _commands = new Db2iBatchCommandCollection(EnsureMutable);
    }

    internal Db2iBatch(Db2iConnection connection, bool ownsConnection) : this(connection)
        => _ownsConnection = ownsConnection;

    public new Db2iBatchCommandCollection BatchCommands => _commands;

    protected override DbBatchCommandCollection DbBatchCommands => _commands;

    /// <summary>Timeout in seconds for each batch execution or reader operation; zero disables it.</summary>
    public override int Timeout
    {
        get => _timeout;
        set
        {
            EnsureMutable();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _timeout = value;
        }
    }

    public new Db2iConnection? Connection
    {
        get { EnsureConnectionAccess(); return _connection; }
        set { EnsureMutable(); EnsureConnectionAccess(); _connection = value; }
    }

    public new Db2iTransaction? Transaction
    {
        get { EnsureConnectionAccess(); return _transaction; }
        set { EnsureMutable(); EnsureConnectionAccess(); _transaction = value; }
    }

    protected override DbConnection? DbConnection
    {
        get => Connection;
        set => Connection = value switch
        {
            null => null,
            Db2iConnection connection => connection,
            _ => throw new ArgumentException("La connessione deve essere di tipo Db2iConnection.", nameof(value)),
        };
    }

    protected override DbTransaction? DbTransaction
    {
        get => Transaction;
        set => Transaction = value switch
        {
            null => null,
            Db2iTransaction transaction => transaction,
            _ => throw new ArgumentException("La transazione deve essere di tipo Db2iTransaction.", nameof(value)),
        };
    }

    public new Db2iBatchCommand CreateBatchCommand()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new();
    }

    protected override DbBatchCommand CreateDbBatchCommand() => CreateBatchCommand();

    public override void Cancel()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cancellation?.Cancel();
        }
    }

    public override void Prepare() => PrepareAsync().GetAwaiter().GetResult();

    public override async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (_ownsConnection)
        {
            throw new NotSupportedException("Prepare richiede un batch associato a una connessione esplicita.");
        }

        var commands = BeginExecution();
        try
        {
            await RunOperationAsync(async token =>
            {
                await ConfigureCommandsAsync(commands, token).ConfigureAwait(false);
                foreach (var item in commands)
                {
                    ValidateExecutionSession();
                    _currentCommand = item;
                    await item.Command.PrepareAsync(token).ConfigureAwait(false);
                }

                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CompleteAsync(closeConnection: false).ConfigureAwait(false);
        }
    }

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync().GetAwaiter().GetResult();

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
        => (int)(await ExecuteAllAsync(scalar: false, cancellationToken).ConfigureAwait(false))!;

    public override object? ExecuteScalar() => ExecuteScalarAsync().GetAwaiter().GetResult();

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
        => ExecuteAllAsync(scalar: true, cancellationToken);

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
    {
        Db2iCommand.ValidateBehavior(behavior);
        var commands = BeginExecution();
        var reader = new Db2iBatchDataReader(this, commands, behavior);
        _reader = reader;
        try
        {
            await RunOperationAsync(async token =>
            {
                await ConfigureCommandsAsync(commands, token).ConfigureAwait(false);
                return await reader.AdvanceCoreAsync(token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return reader;
        }
        catch
        {
            await reader.AbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<object?> ExecuteAllAsync(bool scalar, CancellationToken cancellationToken)
    {
        var commands = BeginExecution();
        var reader = new Db2iBatchDataReader(this, commands, CommandBehavior.Default);
        _reader = reader;
        try
        {
            return await RunOperationAsync<object?>(async token =>
            {
                await ConfigureCommandsAsync(commands, token).ConfigureAwait(false);
                object? value = null;
                var firstResult = true;
                while (await reader.AdvanceCoreAsync(token).ConfigureAwait(false))
                {
                    if (scalar && firstResult && await reader.ReadCoreAsync(token).ConfigureAwait(false))
                    {
                        value = reader.GetValue(0);
                    }

                    firstResult = false;
                }

                return scalar ? value : reader.RecordsAffected;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Execution already completed or failed. Never execute more SQL from cleanup here.
            await reader.AbortAsync().ConfigureAwait(false);
        }
    }

    private Db2iBatchCommand[] BeginExecution()
    {
        lock (_syncRoot)
        {
            EnsureMutable();
            if (_connection is null)
            {
                throw new InvalidOperationException("Il batch non ha una connessione associata.");
            }

            if (_commands.Count == 0)
            {
                throw new InvalidOperationException("Il batch non contiene comandi.");
            }

            var commands = _commands.Cast<Db2iBatchCommand>().ToArray();
            foreach (var item in commands)
            {
                item.SetRecordsAffected(-1);
            }

            _busy = true;
            _cancellation = new CancellationTokenSource();
            return commands;
        }
    }

    private async Task ConfigureCommandsAsync(Db2iBatchCommand[] commands, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_ownsConnection)
        {
            await _connection!.OpenAsync(token).ConfigureAwait(false);
        }

        _connection!.EnsureOpen();
        _connection.ValidateCommandTransaction(_transaction);
        _executionSession = _connection.GetOpenSession();
        // Validate every statement before executing any SQL, so local errors cannot
        // leave an avoidable partially executed autocommit batch.
        foreach (var item in commands)
        {
            _currentCommand = item;
            var command = item.Command;
            _ownedCommands.Add(command);
            command.Connection = _connection;
            command.Transaction = _transaction;
            // The batch owns the deadline across commands. Keep it out of cached
            // server statements so changing Timeout also affects prepared batches.
            command.CommandTimeout = 0;
            command.EnsureCanExecute();
            foreach (Db2iParameter parameter in command.Parameters)
            {
                if (parameter.Direction != ParameterDirection.Input)
                {
                    throw new NotSupportedException("Il batch supporta soltanto parametri di input.");
                }
            }
        }

        _currentCommand = null;
    }

    internal void SetCurrentCommand(Db2iBatchCommand command) => _currentCommand = command;

    internal bool HasExecutionSession
        => _executionSession is not null
            && _connection?.State == ConnectionState.Open
            && ReferenceEquals(_executionSession, _connection.GetOpenSession());

    internal void ValidateExecutionSession()
    {
        if (!HasExecutionSession)
        {
            throw new InvalidOperationException("La connessione del batch è stata chiusa o riaperta durante l'esecuzione.");
        }
        _connection!.ValidateCommandTransaction(_transaction);
    }

    internal Task<T> RunOperationAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken callerToken)
    {
        CancellationToken manualToken;
        lock (_syncRoot)
        {
            if (_operationActive)
            {
                throw new InvalidOperationException("Il batch ha già un'operazione attiva.");
            }

            manualToken = _cancellation!.Token;
            _operationActive = true;
        }

        return RunRegisteredOperationAsync(action, callerToken, manualToken);
    }

    private async Task<T> RunRegisteredOperationAsync<T>(
        Func<CancellationToken, Task<T>> action, CancellationToken callerToken, CancellationToken manualToken)
    {
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? linked = null;
        try
        {
            timeout = Timeout > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(Timeout)) : null;
            linked = timeout is null
                ? CancellationTokenSource.CreateLinkedTokenSource(callerToken, manualToken)
                : CancellationTokenSource.CreateLinkedTokenSource(callerToken, manualToken, timeout.Token);
            linked.Token.ThrowIfCancellationRequested();
            return await action(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Il batch è stato annullato dal chiamante.", exception, callerToken);
        }
        catch (OperationCanceledException exception) when (timeout?.IsCancellationRequested == true)
        {
            throw new Db2iException("Timeout durante l'esecuzione del batch IBM i.",
                Db2iErrorKind.Timeout, exception, isTransient: true)
            {
                FailedBatchCommand = _currentCommand,
            };
        }
        catch (Db2iException exception)
        {
            exception.FailedBatchCommand = _currentCommand;
            throw;
        }
        finally
        {
            linked?.Dispose();
            timeout?.Dispose();
            lock (_syncRoot) { _operationActive = false; }
        }
    }

    internal async ValueTask CompleteAsync(bool closeConnection)
    {
        try
        {
            if ((_ownsConnection || closeConnection) && _connection is not null
                && (_executionSession is null || HasExecutionSession))
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                _reader = null;
                _currentCommand = null;
                _executionSession = null;
                _cancellation?.Dispose();
                _cancellation = null;
                _busy = false;
            }
        }
    }

    public override void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public override async ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_disposed) { return; }
            if (_operationActive)
            {
                throw new InvalidOperationException("Annullare e attendere l'operazione prima di eliminare il batch.");
            }
            _disposed = true;
        }

        try
        {
            if (_reader is not null)
            {
                await _reader.CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var command in _ownedCommands)
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }

            if (_ownsConnection && _connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void EnsureMutable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_busy)
        {
            throw new InvalidOperationException("Il batch è in esecuzione o ha un data reader attivo.");
        }
    }

    private void EnsureConnectionAccess()
    {
        if (_ownsConnection)
        {
            throw new NotSupportedException("La connessione e la transazione sono gestite da Db2iDataSource.");
        }
    }
}
