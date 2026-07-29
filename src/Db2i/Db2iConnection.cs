using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Authentication;
using Db2i.Protocol;

namespace Db2i;

/// <summary>Represents a connection to the database host server on IBM i.</summary>
public sealed class Db2iConnection : DbConnection
{
    private readonly object _syncRoot = new();
    private string _connectionString = string.Empty;
    private ConnectionState _state = ConnectionState.Closed;
    private Db2iConnectionLease? _sessionLease;
    private readonly Db2iSessionPool? _fixedPool;
    private readonly Db2iDataSourceLifetime? _dataSourceLifetime;
    private Db2iConnectionSettings? _openSettings;
    private Db2iTransaction? _activeTransaction;
    private bool _transactionStarting;
    private CancellationTokenSource? _openCancellation;
    private bool _disposed;

    public Db2iConnection()
    {
    }

    public Db2iConnection(string connectionString)
    {
        ConnectionString = connectionString;
    }

    internal Db2iConnection(
        string connectionString,
        Db2iSessionPool? fixedPool,
        Db2iDataSourceLifetime dataSourceLifetime)
    {
        _connectionString = connectionString;
        _fixedPool = fixedPool;
        _dataSourceLifetime = dataSourceLifetime;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get
        {
            lock (_syncRoot)
            {
                return _connectionString;
            }
        }
        set
        {
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_state != ConnectionState.Closed)
                {
                    throw new InvalidOperationException(
                        "La connection string non può essere modificata mentre la connessione è aperta.");
                }

                if (_dataSourceLifetime is not null
                    && !string.Equals(_connectionString, value ?? string.Empty, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "La connection string di una connessione creata da Db2iDataSource è immutabile.");
                }

                _connectionString = value ?? string.Empty;
            }
        }
    }

    public override string Database
    {
        get
        {
            lock (_syncRoot)
            {
                return _sessionLease?.Session.ServerInfo.RelationalDatabaseName
                    ?? GetSettingsOrDefaultCore()?.Database
                    ?? string.Empty;
            }
        }
    }

    public override string DataSource
    {
        get
        {
            lock (_syncRoot)
            {
                return GetSettingsOrDefaultCore()?.Server ?? string.Empty;
            }
        }
    }

    public override string ServerVersion => GetOpenServerInfo().Version.ToString();

    public override ConnectionState State
    {
        get
        {
            lock (_syncRoot)
            {
                return _state;
            }
        }
    }

    public override int ConnectionTimeout
    {
        get
        {
            lock (_syncRoot)
            {
                return GetSettingsOrDefaultCore() is { } settings
                    ? checked((int)settings.ConnectTimeout.TotalSeconds)
                    : 15;
            }
        }
    }

    /// <summary>The CCSID selected by the IBM i database host server.</summary>
    public int ServerCcsid => GetOpenServerInfo().Ccsid;

    /// <summary>The database host-server functional level.</summary>
    public string ServerFunctionalLevel => GetOpenServerInfo().FunctionalLevel;

    /// <summary>The IBM i qualified job identifier in number/user/name form.</summary>
    public string ServerJobIdentifier => GetOpenServerInfo().JobIdentifier;

    /// <summary>Clears the physical session pool associated with a connection.</summary>
    public static void ClearPool(Db2iConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.GetAssociatedPool()?.Clear();
    }

    /// <summary>Clears every global physical session pool managed by the provider.</summary>
    public static void ClearAllPools()
        => Db2iSessionPoolManager.ClearAll();

    public override void ChangeDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        EnsureOpen();
        throw FeatureNotImplemented("ChangeDatabase");
    }

    public override void Close()
        => CloseCoreAsync(discard: false).AsTask().GetAwaiter().GetResult();

    public override async Task CloseAsync()
        => await CloseCoreAsync(discard: false).ConfigureAwait(false);

    public override void Open()
        => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        Db2iConnectionSettings settings;
        CancellationTokenSource? timeoutCancellation = null;
        CancellationTokenSource operationCancellation;

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("La connessione è già aperta o in fase di apertura.");
            }

            settings = new Db2iConnectionStringBuilder(_connectionString).BuildSettings();
            if (settings.ConnectTimeout > TimeSpan.Zero)
            {
                timeoutCancellation = new CancellationTokenSource(settings.ConnectTimeout);
            }

            operationCancellation = timeoutCancellation is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCancellation.Token);
            _openCancellation = operationCancellation;
            _state = ConnectionState.Connecting;
        }

        Db2iConnectionLease? openedLease = null;
        var notifyOpen = false;
        try
        {
            if (_dataSourceLifetime?.IsDisposed == true)
            {
                throw new ObjectDisposedException(nameof(Db2iDataSource));
            }

            if (settings.Pooling)
            {
                var pool = _fixedPool ?? Db2iSessionPoolManager.GetPool(settings);
                openedLease = await pool.RentAsync(operationCancellation.Token).ConfigureAwait(false);
            }
            else
            {
                openedLease = Db2iConnectionLease.CreateUnpooled(
                    await Db2iSession.OpenAsync(settings, operationCancellation.Token)
                        .ConfigureAwait(false));
            }

            if (_dataSourceLifetime?.IsDisposed == true)
            {
                await openedLease.DiscardAsync().ConfigureAwait(false);
                openedLease = null;
                throw new ObjectDisposedException(nameof(Db2iDataSource));
            }

            var accepted = false;
            lock (_syncRoot)
            {
                if (_state == ConnectionState.Connecting
                    && ReferenceEquals(_openCancellation, operationCancellation))
                {
                    _sessionLease = openedLease;
                    _openSettings = settings;
                    openedLease = null;
                    _openCancellation = null;
                    _state = ConnectionState.Open;
                    accepted = true;
                    notifyOpen = true;
                }
            }

            if (!accepted)
            {
                throw new OperationCanceledException(
                    "L'apertura della connessione è stata annullata.",
                    operationCancellation.Token);
            }

        }
        catch (Exception exception)
        {
            if (openedLease is not null)
            {
                await openedLease.DiscardAsync().ConfigureAwait(false);
            }

            lock (_syncRoot)
            {
                if (ReferenceEquals(_openCancellation, operationCancellation))
                {
                    _openCancellation = null;
                    if (_state == ConnectionState.Connecting)
                    {
                        _state = ConnectionState.Closed;
                    }
                }
            }

            var translated = TranslateOpenException(
                exception,
                cancellationToken,
                timeoutCancellation);
            if (ReferenceEquals(translated, exception))
            {
                throw;
            }

            throw translated;
        }
        finally
        {
            operationCancellation.Dispose();
            timeoutCancellation?.Dispose();
        }

        if (notifyOpen)
        {
            OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));
        }
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => BeginTransactionCoreAsync(isolationLevel, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
        => await BeginTransactionCoreAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

    protected override DbCommand CreateDbCommand() => new Db2iCommand { Connection = this };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var dispose = false;
            lock (_syncRoot)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    dispose = true;
                }
            }

            if (dispose)
            {
                Close();
            }
        }

        base.Dispose(disposing);
    }

    internal void EnsureOpen()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ConnectionState.Open || _sessionLease is null)
            {
                throw new InvalidOperationException("La connessione non è aperta.");
            }
        }
    }

    internal Db2iSession GetOpenSession()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ConnectionState.Open || _sessionLease is null)
            {
                throw new InvalidOperationException("La connessione non è aperta.");
            }

            return _sessionLease.Session;
        }
    }

    internal Task AbortCommandAsync()
        => CloseCoreAsync(discard: true).AsTask();

    internal async ValueTask<bool> CancelPendingOperationAsync()
    {
        Db2iSession session;
        Db2iConnectionSettings settings;
        lock (_syncRoot)
        {
            if (_state != ConnectionState.Open
                || _sessionLease is null
                || _openSettings is null)
            {
                return false;
            }

            session = _sessionLease.Session;
            settings = _openSettings;
        }

        var synchronized = await session.CancelPendingOperationAsync(
                settings,
                TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        if (!synchronized)
        {
            await CloseAsync().ConfigureAwait(false);
        }

        return synchronized;
    }

    internal void ValidateCommandTransaction(Db2iTransaction? transaction)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ConnectionState.Open || _sessionLease is null)
            {
                throw new InvalidOperationException("La connessione non è aperta.");
            }

            if (transaction is not null
                && (!transaction.BelongsTo(this) || !transaction.IsUsable))
            {
                throw new InvalidOperationException(
                    "La transazione del comando non è attiva su questa connessione.");
            }

            if (_activeTransaction is null)
            {
                if (transaction is not null)
                {
                    throw new InvalidOperationException(
                        "Il comando specifica una transazione che non è più attiva.");
                }

                return;
            }

            if (!ReferenceEquals(_activeTransaction, transaction))
            {
                throw new InvalidOperationException(
                    "Quando la connessione ha una transazione locale attiva, " +
                    "il comando deve impostare la stessa Db2iTransaction.");
            }
        }
    }

    internal async ValueTask CompleteTransactionAsync(
        Db2iTransaction transaction,
        bool commit,
        CancellationToken cancellationToken)
    {
        Db2iSession session;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ConnectionState.Open
                || _sessionLease is null
                || !ReferenceEquals(_activeTransaction, transaction))
            {
                throw new InvalidOperationException(
                    "La transazione non è attiva su questa connessione.");
            }

            session = _sessionLease.Session;
        }

        Db2iSessionLease? lease = null;
        var boundaryCompleted = false;
        try
        {
            lease = await session.AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
            if (commit)
            {
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            boundaryCompleted = true;
            await session.SetTransactionModeAsync(
                    autoCommit: true,
                    commitmentControlLevel: 0,
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_syncRoot)
            {
                if (ReferenceEquals(_activeTransaction, transaction))
                {
                    _activeTransaction = null;
                }
            }
        }
        catch (Exception exception)
        {
            if (boundaryCompleted || IsConnectionFailure(exception))
            {
                await AbortCommandAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    internal static Db2iException FeatureNotImplemented(string feature)
        => new($"La funzionalità '{feature}' non è ancora implementata.");

    private Db2iServerInfo GetOpenServerInfo()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ConnectionState.Open || _sessionLease is null)
            {
                throw new InvalidOperationException("La connessione non è aperta.");
            }

            return _sessionLease.Session.ServerInfo;
        }
    }

    private async ValueTask CloseCoreAsync(bool discard)
    {
        var (lease, rollbackRequired, notify) = TransitionToClosed();
        if (lease is not null)
        {
            if (discard)
            {
                await lease.DiscardAsync().ConfigureAwait(false);
            }
            else
            {
                await lease.ReturnAsync(rollbackRequired).ConfigureAwait(false);
            }
        }

        if (notify)
        {
            OnStateChange(new StateChangeEventArgs(ConnectionState.Open, ConnectionState.Closed));
        }
    }

    private (Db2iConnectionLease? Lease, bool RollbackRequired, bool Notify) TransitionToClosed()
    {
        Db2iTransaction? transaction;
        Db2iConnectionLease? lease;
        bool notify;
        lock (_syncRoot)
        {
            if (_state == ConnectionState.Closed)
            {
                return (null, false, false);
            }

            notify = _state == ConnectionState.Open;
            _state = ConnectionState.Closed;
            _openCancellation?.Cancel();
            _openCancellation = null;
            lease = _sessionLease;
            _sessionLease = null;
            _openSettings = null;
            _transactionStarting = false;
            transaction = _activeTransaction;
            _activeTransaction = null;
        }

        transaction?.ConnectionClosed();
        return (lease, transaction is not null, notify);
    }

    private async ValueTask<Db2iTransaction> BeginTransactionCoreAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        var effectiveIsolation = isolationLevel == IsolationLevel.Unspecified
            ? IsolationLevel.ReadCommitted
            : isolationLevel;
        var commitmentControlLevel = MapCommitmentControlLevel(effectiveIsolation);
        Db2iSession session;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ConnectionState.Open || _sessionLease is null)
            {
                throw new InvalidOperationException("La connessione non è aperta.");
            }

            if (_activeTransaction is not null || _transactionStarting)
            {
                throw new InvalidOperationException(
                    "La connessione ha già una transazione locale attiva.");
            }

            _transactionStarting = true;
            session = _sessionLease.Session;
        }

        try
        {
            await using var lease = await session
                .AcquireOperationAsync(cancellationToken)
                .ConfigureAwait(false);
            await session.SetTransactionModeAsync(
                    autoCommit: false,
                    commitmentControlLevel,
                    cancellationToken)
                .ConfigureAwait(false);

            var transaction = new Db2iTransaction(this, effectiveIsolation);
            lock (_syncRoot)
            {
                if (_state != ConnectionState.Open
                    || _sessionLease is null
                    || !ReferenceEquals(_sessionLease.Session, session))
                {
                    throw new InvalidOperationException(
                        "La connessione è stata chiusa durante l'avvio della transazione.");
                }

                _activeTransaction = transaction;
                return transaction;
            }
        }
        catch (Exception exception)
        {
            if (IsConnectionFailure(exception))
            {
                await AbortCommandAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            lock (_syncRoot)
            {
                _transactionStarting = false;
            }
        }
    }

    private static short MapCommitmentControlLevel(IsolationLevel isolationLevel)
        => isolationLevel switch
        {
            IsolationLevel.ReadUncommitted => 2,
            IsolationLevel.ReadCommitted => 1,
            IsolationLevel.RepeatableRead => 3,
            IsolationLevel.Serializable => 4,
            IsolationLevel.Chaos or IsolationLevel.Snapshot => throw new NotSupportedException(
                $"Il livello di isolamento {isolationLevel} non è supportato da IBM i M3."),
            _ => throw new ArgumentOutOfRangeException(nameof(isolationLevel)),
        };

    private static bool IsConnectionFailure(Exception exception)
        => exception is IOException
            or SocketException
            or ObjectDisposedException
            or OperationCanceledException;

    private static Exception TranslateOpenException(
        Exception exception,
        CancellationToken callerCancellation,
        CancellationTokenSource? timeoutCancellation)
    {
        if (exception is Db2iException)
        {
            return exception;
        }

        if (exception is OperationCanceledException)
        {
            if (callerCancellation.IsCancellationRequested)
            {
                return new OperationCanceledException(
                    "L'apertura della connessione è stata annullata dal chiamante.",
                    exception,
                    callerCancellation);
            }

            if (timeoutCancellation?.IsCancellationRequested == true)
            {
                return new Db2iException(
                    "Timeout durante l'apertura della connessione IBM i.",
                    Db2iErrorKind.Timeout,
                    exception,
                    isTransient: true);
            }

            return exception;
        }

        if (exception is AuthenticationException)
        {
            return new Db2iException(
                "La negoziazione TLS con IBM i non è riuscita.",
                Db2iErrorKind.Tls,
                exception);
        }

        if (exception is InvalidDataException)
        {
            return new Db2iException(
                "IBM i ha restituito un data stream Client Access non valido.",
                Db2iErrorKind.Protocol,
                exception);
        }

        if (exception is SocketException or IOException)
        {
            return new Db2iException(
                "Impossibile stabilire o mantenere la connessione al database host server IBM i.",
                Db2iErrorKind.Connection,
                exception,
                isTransient: true);
        }

        return exception;
    }

    private Db2iConnectionSettings? GetSettingsOrDefaultCore()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return null;
        }

        try
        {
            return new Db2iConnectionStringBuilder(_connectionString).BuildSettings();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private Db2iSessionPool? GetAssociatedPool()
    {
        string connectionString;
        lock (_syncRoot)
        {
            if (_sessionLease?.Pool is { } leasedPool)
            {
                return leasedPool;
            }

            if (_fixedPool is not null)
            {
                return _fixedPool;
            }

            connectionString = _connectionString;
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var settings = new Db2iConnectionStringBuilder(connectionString).BuildSettings();
        return settings.Pooling ? Db2iSessionPoolManager.TryGetPool(settings) : null;
    }
}
