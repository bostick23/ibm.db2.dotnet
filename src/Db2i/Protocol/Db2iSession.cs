using System.Security.Cryptography;

namespace Db2i.Protocol;

internal sealed class Db2iSession : IAsyncDisposable, IDisposable
{
    private readonly Db2iTransport _transport;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _handleLock = new();
    private readonly object _pendingReplyLock = new();
    private readonly object _statementLock = new();
    private readonly HashSet<Db2iPreparedStatement> _statements = [];
    private ushort _nextStatementHandle = 3;
    private int? _pendingReplyCorrelationId;
    private bool _deletePendingRpbOnCancel;
    private bool _serverAllowsLocatorPersistenceChange = true;
    private short _locatorPersistence;
    private bool _autoCommit = true;
    private bool _disposed;

    private Db2iSession(Db2iTransport transport, Db2iServerInfo serverInfo)
    {
        _transport = transport;
        ServerInfo = serverInfo;
    }

    internal Db2iServerInfo ServerInfo { get; }

    internal bool IsPotentiallyUsable
        => !_disposed && !HasPendingReply && _transport.IsPotentiallyUsable;

    internal Db2iSessionLease AcquireOperation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_operationGate.Wait(0))
        {
            throw new InvalidOperationException(
                "La connessione è occupata da un altro comando o da un data reader attivo.");
        }

        return new Db2iSessionLease(_operationGate);
    }

    internal async ValueTask<Db2iSessionLease> AcquireOperationAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "La connessione è occupata da un altro comando o da un data reader attivo.");
        }

        return new Db2iSessionLease(_operationGate);
    }

    internal async ValueTask<Db2iPreparedStatement> PrepareAsync(
        string commandText,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var handle = AllocateStatementHandle();
        var statementKind = SqlProtocol.ClassifyStatement(commandText);
        var rpbCreated = false;
        try
        {
            await SendCancelableRequestAsync(
                    SqlProtocol.CreateRpbRequest(
                        handle,
                        ServerInfo.Ccsid,
                        commandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            rpbCreated = true;

            await SendCancelableRequestAsync(
                    SqlProtocol.CreatePrepareRequest(handle, commandText, statementKind),
                    cancellationToken)
                .ConfigureAwait(false);
            var reply = await ReceiveCancelableReplyAsync(
                    handle,
                    cancellationToken,
                    deleteRpbOnCancel: true)
                .ConfigureAwait(false);
            EnsureSqlSuccessful(reply, SqlProtocol.PrepareDescribe, "prepare SQL", allowEndOfData: false);

            var resultFormat = SqlProtocol.ParseResultFormat(reply, ServerInfo.Ccsid);
            if (statementKind == Db2iStatementKind.Query && resultFormat is null)
            {
                throw ClientAccessCodePoints.ProtocolError(
                    "IBM i did not return a result descriptor for the query. " +
                    $"Returned code points: {string.Join(
                        ", ",
                        reply.CodePoints.Select(value => $"0x{value.CodePoint:X4}"))}.");
            }

            var parameterFormat = SqlProtocol.ParseParameterFormat(reply, ServerInfo.Ccsid);
            var statement = new Db2iPreparedStatement(
                this,
                handle,
                commandText,
                resultFormat,
                parameterFormat,
                statementKind);
            lock (_statementLock)
            {
                _statements.Add(statement);
            }

            return statement;
        }
        catch (Exception exception)
        {
            if (rpbCreated
                && !_disposed
                && (exception is not OperationCanceledException || !HasPendingReply))
            {
                try
                {
                    await DeleteRpbAsync(handle, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                    when (cleanupException is IOException or ObjectDisposedException)
                {
                }
            }

            throw;
        }
    }

    internal async ValueTask ChangeParameterDescriptorAsync(
        Db2iPreparedStatement statement,
        Db2iDataFormat format,
        CancellationToken cancellationToken)
    {
        EnsureOwnedStatement(statement);
        await SendCancelableRequestAsync(
                SqlProtocol.CreateChangeDescriptorRequest(statement.Handle, format),
                cancellationToken)
            .ConfigureAwait(false);
        statement.SetParameterFormat(format);
    }

    internal async ValueTask<int> ExecuteNonQueryAsync(
        Db2iPreparedStatement statement,
        IReadOnlyList<Db2iParameter> parameters,
        CancellationToken cancellationToken)
    {
        EnsureOwnedStatement(statement);
        if (statement.StatementKind != Db2iStatementKind.NonQuery)
        {
            throw new InvalidOperationException(
                "ExecuteNonQuery può eseguire soltanto statement DML o DDL.");
        }

        await SendCancelableRequestAsync(
                SqlProtocol.CreateExecuteRequest(
                    statement.Handle,
                    parameters.Count == 0 ? null : statement.ParameterFormat,
                    parameters,
                    statement.StatementKind),
                cancellationToken)
            .ConfigureAwait(false);
        var reply = await ReceiveCancelableReplyAsync(statement.Handle, cancellationToken)
            .ConfigureAwait(false);
        EnsureSqlSuccessful(
            reply,
            SqlProtocol.Execute,
            "execute SQL statement",
            allowEndOfData: false);
        return SqlProtocol.ParseDiagnostic(reply, ServerInfo.Ccsid).RowsAffected ?? 0;
    }

    internal async ValueTask SetTransactionModeAsync(
        bool autoCommit,
        short commitmentControlLevel,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = autoCommit ? "enable autocommit" : "start commitment control";
        var requestedLocatorPersistence = commitmentControlLevel == 0 ? (short)0 : (short)1;
        var changeLocatorPersistence = _serverAllowsLocatorPersistenceChange
            && requestedLocatorPersistence != _locatorPersistence;
        var reply = await SendTransactionAttributesAsync(
                autoCommit,
                commitmentControlLevel,
                changeLocatorPersistence,
                cancellationToken)
            .ConfigureAwait(false);
        if (reply.ReturnFunctionId != DatabaseProtocol.SetAttributesRequestId)
        {
            throw ClientAccessCodePoints.ProtocolError(
                $"IBM i returned function 0x{reply.ReturnFunctionId:X4} for {operation}.");
        }

        if ((reply.ErrorClass != 0 || reply.ReturnCode != 0) && changeLocatorPersistence)
        {
            // JTOpen retries without locator persistence when IBM i rejects changing
            // it after a statement has already been executed (commonly return code -601).
            _serverAllowsLocatorPersistenceChange = false;
            reply = await SendTransactionAttributesAsync(
                    autoCommit,
                    commitmentControlLevel,
                    includeLocatorPersistence: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (reply.ReturnFunctionId != DatabaseProtocol.SetAttributesRequestId)
            {
                throw ClientAccessCodePoints.ProtocolError(
                    $"IBM i returned function 0x{reply.ReturnFunctionId:X4} for {operation}.");
            }
        }

        if (reply.ErrorClass != 0 || reply.ReturnCode != 0)
        {
            var diagnostic = await RetrieveDiagnosticAsync(
                    handle: 0,
                    cancellationToken)
                .ConfigureAwait(false);
            throw Db2iErrorFactory.FromDatabaseReply(reply, operation, diagnostic);
        }

        if (changeLocatorPersistence && _serverAllowsLocatorPersistenceChange)
        {
            _locatorPersistence = requestedLocatorPersistence;
        }

        _autoCommit = autoCommit;
    }

    private async ValueTask<DatabaseReply> SendTransactionAttributesAsync(
        bool autoCommit,
        short commitmentControlLevel,
        bool includeLocatorPersistence,
        CancellationToken cancellationToken)
    {
        var correlationId = AllocateControlCorrelationId();
        await _transport.SendAsync(
                DatabaseProtocol.CreateTransactionAttributesRequest(
                    autoCommit,
                    commitmentControlLevel,
                    correlationId,
                    includeLocatorPersistence),
                cancellationToken)
            .ConfigureAwait(false);
        return DatabaseProtocol.ParseReply(
            await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false),
            correlationId);
    }

    internal ValueTask CommitAsync(CancellationToken cancellationToken)
        => CompleteTransactionAsync(SqlProtocol.Commit, "commit", cancellationToken);

    internal ValueTask RollbackAsync(CancellationToken cancellationToken)
        => CompleteTransactionAsync(SqlProtocol.Rollback, "rollback", cancellationToken);

    internal async ValueTask<bool> ResetForPoolingAsync(bool rollbackRequired)
    {
        if (!IsPotentiallyUsable)
        {
            return false;
        }

        Db2iSessionLease? lease = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            try
            {
                lease = AcquireOperation();
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            if (HasPendingReply)
            {
                return false;
            }

            if (rollbackRequired || !_autoCommit)
            {
                await RollbackAsync(timeout.Token).ConfigureAwait(false);
                await SetTransactionModeAsync(
                        autoCommit: true,
                        commitmentControlLevel: 0,
                        timeout.Token)
                    .ConfigureAwait(false);
            }

            Db2iPreparedStatement[] statements;
            lock (_statementLock)
            {
                statements = _statements.ToArray();
            }

            foreach (var statement in statements)
            {
                await DeleteStatementAsync(statement, timeout.Token).ConfigureAwait(false);
            }

            return IsPotentiallyUsable;
        }
        catch (Exception exception)
            when (exception is Db2iException
                or IOException
                or InvalidDataException
                or ObjectDisposedException
                or OperationCanceledException
                or System.Net.Sockets.SocketException)
        {
            return false;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    internal async ValueTask<bool> CancelPendingOperationAsync(
        Db2iConnectionSettings settings,
        TimeSpan drainTimeout)
    {
        int pendingCorrelationId;
        bool deleteRpb;
        lock (_pendingReplyLock)
        {
            if (_pendingReplyCorrelationId is not { } pending)
            {
                return true;
            }

            pendingCorrelationId = pending;
            deleteRpb = _deletePendingRpbOnCancel;
        }

        using var timeout = new CancellationTokenSource(drainTimeout);
        try
        {
            await using var cancellationSession = await OpenAsync(settings, timeout.Token)
                .ConfigureAwait(false);
            var cancelCorrelationId = cancellationSession.AllocateControlCorrelationId();
            await cancellationSession._transport.SendAsync(
                    SqlProtocol.CreateCancelRequest(
                        cancelCorrelationId,
                        cancellationSession.ServerInfo.Ccsid,
                        ServerInfo.JobIdentifier),
                    timeout.Token)
                .ConfigureAwait(false);
            var cancelReply = DatabaseProtocol.ParseReply(
                await cancellationSession._transport
                    .ReceiveAsync(timeout.Token)
                    .ConfigureAwait(false),
                cancelCorrelationId);
            if (cancelReply.ReturnFunctionId != SqlProtocol.Cancel
                || cancelReply.ErrorClass != 0
                || cancelReply.ReturnCode != 0)
            {
                return false;
            }

            _ = DatabaseProtocol.ParseReply(
                await _transport.ReceiveAsync(timeout.Token).ConfigureAwait(false),
                pendingCorrelationId);
            if (deleteRpb)
            {
                await DeleteRpbAsync(
                        checked((ushort)pendingCorrelationId),
                        timeout.Token)
                    .ConfigureAwait(false);
            }

            ClearPendingReply(pendingCorrelationId);
            return true;
        }
        catch (Exception exception)
            when (exception is Db2iException
                or IOException
                or InvalidDataException
                or ObjectDisposedException
                or OperationCanceledException
                or System.Net.Sockets.SocketException
                or System.Security.Authentication.AuthenticationException)
        {
            return false;
        }
    }

    internal async ValueTask<Db2iQueryCursor> OpenCursorAsync(
        Db2iPreparedStatement statement,
        IReadOnlyList<Db2iParameter> parameters,
        bool singleRow,
        CancellationToken cancellationToken)
    {
        EnsureOwnedStatement(statement);
        var resultFormat = statement.ResultFormat
            ?? throw new InvalidOperationException(
                "Lo statement preparato non restituisce un result set.");
        await SendCancelableRequestAsync(
                SqlProtocol.CreateOpenRequest(
                    statement.Handle,
                    resultFormat,
                    parameters.Count == 0 ? null : statement.ParameterFormat,
                    parameters,
                    singleRow),
                cancellationToken)
            .ConfigureAwait(false);
        var reply = await ReceiveCancelableReplyAsync(statement.Handle, cancellationToken)
            .ConfigureAwait(false);
        var endOfData = EnsureSqlSuccessful(
            reply,
            SqlProtocol.OpenDescribeFetch,
            "open/fetch SQL cursor",
            allowEndOfData: true);
        var block = SqlProtocol.ParseResultBlock(reply, resultFormat);
        return new Db2iQueryCursor(
            this,
            statement,
            block,
            endOfData,
            cursorClosedByServer: reply.ErrorClass == 2 && reply.ReturnCode == 700,
            singleRow);
    }

    internal async ValueTask<(Db2iResultBlock Block, bool EndOfData, bool CursorClosed)> FetchAsync(
        Db2iPreparedStatement statement,
        bool singleRow,
        CancellationToken cancellationToken)
    {
        EnsureOwnedStatement(statement);
        var resultFormat = statement.ResultFormat
            ?? throw new InvalidOperationException(
                "Lo statement preparato non restituisce un result set.");
        await SendCancelableRequestAsync(
                SqlProtocol.CreateFetchRequest(
                    statement.Handle,
                    resultFormat,
                    singleRow),
                cancellationToken)
            .ConfigureAwait(false);
        var reply = await ReceiveCancelableReplyAsync(statement.Handle, cancellationToken)
            .ConfigureAwait(false);
        var endOfData = EnsureSqlSuccessful(
            reply,
            SqlProtocol.Fetch,
            "fetch SQL cursor",
            allowEndOfData: true);
        return (
            SqlProtocol.ParseResultBlock(reply, resultFormat),
            endOfData,
            reply.ErrorClass == 2 && reply.ReturnCode == 700);
    }

    internal async ValueTask CloseCursorAsync(
        Db2iPreparedStatement statement,
        CancellationToken cancellationToken)
    {
        EnsureOwnedStatement(statement);
        await _transport.SendAsync(
                SqlProtocol.CreateCloseRequest(statement.Handle),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask DeleteStatementAsync(
        Db2iPreparedStatement statement,
        CancellationToken cancellationToken)
    {
        if (_disposed || statement.IsDeleted)
        {
            statement.MarkDeleted();
            RemoveStatement(statement);
            return;
        }

        EnsureOwnedStatement(statement);
        if (statement.HasClientDescriptor)
        {
            await _transport.SendAsync(
                    SqlProtocol.CreateDeleteDescriptorRequest(statement.Handle),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await DeleteRpbAsync(statement.Handle, cancellationToken).ConfigureAwait(false);
        await _transport.SendAsync(
                SqlProtocol.CreateDeleteResultSetRequest(statement.Handle),
                cancellationToken)
            .ConfigureAwait(false);
        var resultSetReply = DatabaseProtocol.ParseReply(
            await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false),
            statement.Handle);
        EnsureSqlSuccessful(
            resultSetReply,
            SqlProtocol.DeleteResultSet,
            "delete SQL result set",
            allowEndOfData: false);
        statement.MarkDeleted();
        RemoveStatement(statement);
    }

    private async ValueTask DeleteRpbAsync(
        ushort handle,
        CancellationToken cancellationToken)
    {
        await _transport.SendAsync(
                SqlProtocol.CreateDeleteRpbRequest(handle),
                cancellationToken)
            .ConfigureAwait(false);
        var reply = DatabaseProtocol.ParseReply(
            await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false),
            handle);
        EnsureSqlSuccessful(
            reply,
            SqlProtocol.DeleteRpb,
            "delete SQL request parameter block",
            allowEndOfData: false);
    }

    private async ValueTask<Db2iSqlDiagnostic> RetrieveDiagnosticAsync(
        ushort handle,
        CancellationToken cancellationToken)
    {
        var correlationId = AllocateControlCorrelationId();
        await _transport.SendAsync(
                SqlProtocol.CreateDiagnosticResultSetRequest(handle, correlationId),
                cancellationToken)
            .ConfigureAwait(false);
        var reply = DatabaseProtocol.ParseReply(
            await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false),
            correlationId);
        return SqlProtocol.ParseDiagnostic(reply, ServerInfo.Ccsid);
    }

    internal static async ValueTask<Db2iSession> OpenAsync(
        Db2iConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        Db2iTransport? transport = null;
        try
        {
            transport = await Db2iTransport.ConnectAsync(settings, cancellationToken).ConfigureAwait(false);

            var seedRequest = ExchangeRandomSeeds.CreateRequest();
            await transport.SendAsync(seedRequest.Bytes, cancellationToken).ConfigureAwait(false);
            var seedReply = ExchangeRandomSeeds.ParseReply(
                await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false));
            if (seedReply.ReturnCode != 0)
            {
                throw Db2iErrorFactory.FromHostReturnCode(seedReply.ReturnCode, "exchange random seeds");
            }

            var userIdEbcdic = SignonEncoding.EncodeProfile(settings.UserId);
            var passwordSubstitute = PasswordSubstitute.Generate(
                settings.UserId,
                settings.Password,
                seedReply.PasswordLevel,
                seedRequest.ClientSeed,
                seedReply.ServerSeed);
            byte[]? startRequest = null;
            try
            {
                startRequest = StartServer.CreateRequest(userIdEbcdic, passwordSubstitute);
                await transport.SendAsync(startRequest, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(userIdEbcdic);
                CryptographicOperations.ZeroMemory(passwordSubstitute);
                if (startRequest is not null)
                {
                    CryptographicOperations.ZeroMemory(startRequest);
                }

                CryptographicOperations.ZeroMemory(seedRequest.ClientSeed);
                CryptographicOperations.ZeroMemory(seedReply.ServerSeed);
            }

            var startReply = StartServer.ParseReply(
                await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false));
            if (startReply.ReturnCode != 0)
            {
                throw Db2iErrorFactory.FromHostReturnCode(startReply.ReturnCode, "start database server");
            }

            var providerVersion = typeof(Db2iConnection).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            const int bootstrapCorrelationId = 1;
            var attributesRequest = DatabaseProtocol.CreateBootstrapAttributesRequest(
                settings,
                bootstrapCorrelationId,
                providerVersion);
            await transport.SendAsync(attributesRequest, cancellationToken).ConfigureAwait(false);
            var attributesReply = DatabaseProtocol.ParseReply(
                await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false),
                bootstrapCorrelationId);
            EnsureSuccessful(attributesReply, "set bootstrap attributes");

            if (attributesReply.ServerAttributes is not { } attributes)
            {
                throw ClientAccessCodePoints.ProtocolError(
                    "IBM i did not return the requested database server attributes.");
            }

            var serverInfo = DatabaseProtocol.ParseServerInfo(attributes);

            if (!string.IsNullOrWhiteSpace(settings.DefaultCollection))
            {
                const int collectionCorrelationId = 2;
                var collectionRequest = DatabaseProtocol.CreateDefaultCollectionRequest(
                    settings.DefaultCollection,
                    serverInfo.Ccsid,
                    collectionCorrelationId);
                await transport.SendAsync(collectionRequest, cancellationToken).ConfigureAwait(false);
                var collectionReply = DatabaseProtocol.ParseReply(
                    await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false),
                    collectionCorrelationId);
                EnsureSuccessful(collectionReply, "set default collection");
            }

            var session = new Db2iSession(transport, serverInfo);
            transport = null;
            return session;
        }
        finally
        {
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        MarkStatementsDeleted();
        _transport.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        MarkStatementsDeleted();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private static void EnsureSuccessful(DatabaseReply reply, string operation)
    {
        if (reply.ReturnFunctionId != DatabaseProtocol.SetAttributesRequestId)
        {
            throw ClientAccessCodePoints.ProtocolError(
                $"IBM i returned function 0x{reply.ReturnFunctionId:X4} for {operation}.");
        }

        if (reply.ErrorClass != 0 || reply.ReturnCode != 0)
        {
            throw Db2iErrorFactory.FromDatabaseReply(reply, operation);
        }
    }

    private ushort AllocateStatementHandle()
    {
        lock (_handleLock)
        {
            if (_nextStatementHandle == 0)
            {
                _nextStatementHandle = 3;
            }

            return _nextStatementHandle++;
        }
    }

    private int AllocateControlCorrelationId()
    {
        lock (_handleLock)
        {
            if (_nextStatementHandle == 0)
            {
                _nextStatementHandle = 3;
            }

            return _nextStatementHandle++;
        }
    }

    private bool HasPendingReply
    {
        get
        {
            lock (_pendingReplyLock)
            {
                return _pendingReplyCorrelationId is not null;
            }
        }
    }

    private async ValueTask SendCancelableRequestAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Once a Client Access packet starts, finish writing it so cancellation
        // can never leave a partial packet on the primary stream.
        await _transport.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<DatabaseReply> ReceiveCancelableReplyAsync(
        int correlationId,
        CancellationToken cancellationToken,
        bool deleteRpbOnCancel = false)
    {
        lock (_pendingReplyLock)
        {
            if (_pendingReplyCorrelationId is not null)
            {
                throw ClientAccessCodePoints.ProtocolError(
                    "A database reply is already pending on this IBM i session.");
            }

            _pendingReplyCorrelationId = correlationId;
            _deletePendingRpbOnCancel = deleteRpbOnCancel;
        }

        try
        {
            var reply = DatabaseProtocol.ParseReply(
                await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false),
                correlationId);
            ClearPendingReply(correlationId);
            return reply;
        }
        catch (OperationCanceledException)
        {
            // The command issues CANCEL through a second authenticated session and
            // drains this reply before allowing the primary session to be reused.
            throw;
        }
        catch
        {
            ClearPendingReply(correlationId);
            throw;
        }
    }

    private void ClearPendingReply(int correlationId)
    {
        lock (_pendingReplyLock)
        {
            if (_pendingReplyCorrelationId == correlationId)
            {
                _pendingReplyCorrelationId = null;
                _deletePendingRpbOnCancel = false;
            }
        }
    }

    private void RemoveStatement(Db2iPreparedStatement statement)
    {
        lock (_statementLock)
        {
            _statements.Remove(statement);
        }
    }

    private void MarkStatementsDeleted()
    {
        Db2iPreparedStatement[] statements;
        lock (_statementLock)
        {
            statements = _statements.ToArray();
            _statements.Clear();
        }

        foreach (var statement in statements)
        {
            statement.MarkDeleted();
        }
    }

    private async ValueTask CompleteTransactionAsync(
        ushort functionId,
        string operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var correlationId = AllocateControlCorrelationId();
        await _transport.SendAsync(
                SqlProtocol.CreateTransactionBoundaryRequest(functionId, correlationId),
                cancellationToken)
            .ConfigureAwait(false);
        var reply = DatabaseProtocol.ParseReply(
            await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false),
            correlationId);
        EnsureSqlSuccessful(reply, functionId, operation, allowEndOfData: false);
    }

    private bool EnsureSqlSuccessful(
        DatabaseReply reply,
        ushort expectedFunctionId,
        string operation,
        bool allowEndOfData)
    {
        if (reply.ReturnFunctionId != expectedFunctionId)
        {
            throw ClientAccessCodePoints.ProtocolError(
                $"IBM i returned function 0x{reply.ReturnFunctionId:X4} for {operation}; " +
                $"expected 0x{expectedFunctionId:X4}.");
        }

        var diagnostic = SqlProtocol.ParseDiagnostic(reply, ServerInfo.Ccsid);
        var endOfData = SqlProtocol.IsEndOfData(reply, diagnostic);
        if (endOfData && allowEndOfData)
        {
            return true;
        }

        if (diagnostic.SqlCode < 0 || reply.ReturnCode < 0)
        {
            throw new Db2iException(
                diagnostic.BuildMessage(operation, reply.ErrorClass, reply.ReturnCode),
                Db2iErrorKind.Sql,
                hostReturnCode: reply.ReturnCode,
                errorClass: reply.ErrorClass,
                sqlCode: diagnostic.SqlCode,
                sqlState: diagnostic.SqlState);
        }

        var isWarning = diagnostic.SqlCode > 0 || reply.ReturnCode > 0;
        if (reply.ErrorClass != 0 && !endOfData && !isWarning)
        {
            throw new Db2iException(
                diagnostic.BuildMessage(operation, reply.ErrorClass, reply.ReturnCode),
                Db2iErrorKind.Server,
                hostReturnCode: reply.ReturnCode,
                errorClass: reply.ErrorClass,
                sqlCode: diagnostic.SqlCode,
                sqlState: diagnostic.SqlState);
        }

        return endOfData;
    }

    private void EnsureOwnedStatement(Db2iPreparedStatement statement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(statement);
        if (!ReferenceEquals(statement.Session, this) || statement.IsDeleted)
        {
            throw new InvalidOperationException(
                "Lo statement preparato non appartiene a questa sessione IBM i.");
        }
    }
}

internal sealed class Db2iSessionLease : IDisposable, IAsyncDisposable
{
    private SemaphoreSlim? _gate;

    internal Db2iSessionLease(SemaphoreSlim gate)
    {
        _gate = gate;
    }

    public void Dispose()
        => Interlocked.Exchange(ref _gate, null)?.Release();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class Db2iPreparedStatement
{
    private bool _deleted;

    internal Db2iPreparedStatement(
        Db2iSession session,
        ushort handle,
        string commandText,
        Db2iDataFormat? resultFormat,
        Db2iDataFormat? parameterFormat,
        Db2iStatementKind statementKind)
    {
        Session = session;
        Handle = handle;
        CommandText = commandText;
        ResultFormat = resultFormat;
        ParameterFormat = parameterFormat;
        StatementKind = statementKind;
    }

    internal Db2iSession Session { get; }

    internal ushort Handle { get; }

    internal string CommandText { get; }

    internal Db2iDataFormat? ResultFormat { get; }

    internal Db2iDataFormat? ParameterFormat { get; private set; }

    internal Db2iStatementKind StatementKind { get; }

    internal bool HasClientDescriptor { get; private set; }

    internal bool IsDeleted => _deleted;

    internal void SetParameterFormat(Db2iDataFormat format)
    {
        ParameterFormat = format;
        HasClientDescriptor = true;
    }

    internal void MarkDeleted() => _deleted = true;
}

internal sealed class Db2iQueryCursor
{
    private readonly Db2iSession _session;
    private readonly Db2iPreparedStatement _statement;
    private readonly bool _singleRow;
    private bool _cursorClosed;

    internal Db2iQueryCursor(
        Db2iSession session,
        Db2iPreparedStatement statement,
        Db2iResultBlock firstBlock,
        bool endOfData,
        bool cursorClosedByServer,
        bool singleRow)
    {
        _session = session;
        _statement = statement;
        CurrentBlock = firstBlock;
        EndOfData = endOfData;
        _cursorClosed = cursorClosedByServer;
        _singleRow = singleRow;
    }

    internal Db2iResultBlock CurrentBlock { get; private set; }

    internal bool EndOfData { get; private set; }

    internal async ValueTask<Db2iResultBlock> FetchAsync(CancellationToken cancellationToken)
    {
        if (EndOfData || _singleRow)
        {
            EndOfData = true;
            CurrentBlock = Db2iResultBlock.Empty;
            return CurrentBlock;
        }

        var result = await _session.FetchAsync(_statement, _singleRow, cancellationToken)
            .ConfigureAwait(false);
        CurrentBlock = result.Block;
        EndOfData = result.EndOfData;
        _cursorClosed |= result.CursorClosed;
        return CurrentBlock;
    }

    internal async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_cursorClosed)
        {
            return;
        }

        _cursorClosed = true;
        await _session.CloseCursorAsync(_statement, cancellationToken).ConfigureAwait(false);
    }
}
