using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Db2i.Protocol;

namespace Db2i.Tests;

internal sealed class FakeIbmIHostServer : IAsyncDisposable
{
    private static readonly byte[] ServerSeed = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _serverTask;
    private readonly bool _useTls;
    private readonly byte _passwordLevel;
    private readonly int _startReturnCode;
    private readonly bool _expectDefaultCollection;
    private readonly bool _stallAfterAccept;
    private readonly bool _allowTlsAuthenticationFailure;
    private readonly FakeQueryScenario? _queryScenario;
    private readonly IReadOnlyDictionary<string, FakeQueryScenario>? _queryScenarios;
    private readonly X509Certificate2? _certificate;
    private readonly TaskCompletionSource _cancelReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _locatorPersistenceRejected;
    private int _acceptedConnectionCount;
    private int _closedConnectionCount;
    private int _activeConnectionCount;
    private int _maximumActiveConnectionCount;

    internal FakeIbmIHostServer(
        bool useTls = false,
        byte passwordLevel = 3,
        int startReturnCode = 0,
        bool expectDefaultCollection = false,
        bool stallAfterAccept = false,
        bool allowTlsAuthenticationFailure = false,
        FakeQueryScenario? queryScenario = null,
        IReadOnlyDictionary<string, FakeQueryScenario>? queryScenarios = null)
    {
        _useTls = useTls;
        _passwordLevel = passwordLevel;
        _startReturnCode = startReturnCode;
        _expectDefaultCollection = expectDefaultCollection;
        _stallAfterAccept = stallAfterAccept;
        _allowTlsAuthenticationFailure = allowTlsAuthenticationFailure;
        _queryScenario = queryScenario;
        _queryScenarios = queryScenarios;
        _certificate = useTls ? CreateCertificate() : null;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serverTask = RunAsync();
    }

    internal int Port { get; }

    internal byte PasswordIndicator { get; private set; }

    internal string? RequestedDefaultCollection { get; private set; }

    internal string? PreparedSql { get; private set; }

    internal short? PreparedStatementType { get; private set; }

    internal IReadOnlyList<string> PreparedStatements => _preparedStatements.ToArray();

    internal IReadOnlyList<string> ExecutedSql => _executedSql.ToArray();

    internal IReadOnlyList<ReadOnlyMemory<byte>?> ExecutedParameters
        => _executedParameters.ToArray();

    internal IReadOnlyList<ushort> SqlRequests => _sqlRequests.ToArray();

    internal string? CancelTargetJobIdentifier { get; private set; }

    internal int FetchCount { get; private set; }

    internal int AcceptedConnectionCount => Volatile.Read(ref _acceptedConnectionCount);

    internal int ClosedConnectionCount => Volatile.Read(ref _closedConnectionCount);

    internal int ActiveConnectionCount => Volatile.Read(ref _activeConnectionCount);

    internal int MaximumActiveConnectionCount => Volatile.Read(ref _maximumActiveConnectionCount);

    internal ReadOnlyMemory<byte>? LastParameterData { get; private set; }

    internal Db2iDataFormat? LastClientParameterFormat { get; private set; }

    internal int? LobThreshold { get; private set; }

    internal byte? CloseOnEof { get; private set; }

    internal IReadOnlyList<FakeLobRequest> LobReads => _lobReads.ToArray();

    internal IReadOnlyList<FakeLobRequest> LobWrites => _lobWrites.ToArray();

    internal IReadOnlyList<int> FreedLobHandles => _freedLobHandles.ToArray();

    internal IReadOnlyDictionary<int, byte[]> UploadedLobs => _uploadedLobs;

    internal IReadOnlyList<IReadOnlyDictionary<int, byte[]>> ExecutedLobs => _executedLobs.ToArray();

    internal IReadOnlyList<(bool AutoCommit, short CommitmentLevel)> TransactionModes
        => _transactionModes;

    internal IReadOnlyList<short?> TransactionLocatorPersistence
        => _transactionLocatorPersistence;

    private readonly ConcurrentQueue<ushort> _sqlRequests = new();
    private readonly ConcurrentQueue<string> _preparedStatements = new();
    private readonly ConcurrentQueue<string> _executedSql = new();
    private readonly ConcurrentQueue<ReadOnlyMemory<byte>?> _executedParameters = new();
    private readonly ConcurrentQueue<FakeLobRequest> _lobReads = new();
    private readonly ConcurrentQueue<FakeLobRequest> _lobWrites = new();
    private readonly ConcurrentQueue<int> _freedLobHandles = new();
    private readonly ConcurrentDictionary<int, byte[]> _uploadedLobs = new();
    private readonly ConcurrentQueue<IReadOnlyDictionary<int, byte[]>> _executedLobs = new();
    private readonly List<(bool AutoCommit, short CommitmentLevel)> _transactionModes = [];
    private readonly List<short?> _transactionLocatorPersistence = [];

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (IOException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _certificate?.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task RunAsync()
    {
        var clients = new List<Task>();
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token)
                    .ConfigureAwait(false);
                clients.Add(HandleClientAsync(client));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await Task.WhenAll(clients).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (IOException) when (_shutdown.IsCancellationRequested)
            {
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        var connectionNumber = Interlocked.Increment(ref _acceptedConnectionCount);
        var active = Interlocked.Increment(ref _activeConnectionCount);
        UpdateMaximumActiveConnections(active);
        try
        {
            using (client)
            {
                Stream stream = client.GetStream();
                if (_useTls)
                {
                    var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                    try
                    {
                        await sslStream.AuthenticateAsServerAsync(
                                new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = _certificate,
                                    EnabledSslProtocols = SslProtocols.Tls12,
                                },
                                _shutdown.Token)
                            .ConfigureAwait(false);
                    }
                    catch (AuthenticationException) when (_allowTlsAuthenticationFailure)
                    {
                        await sslStream.DisposeAsync().ConfigureAwait(false);
                        return;
                    }

                    stream = sslStream;
                }

                await using (stream.ConfigureAwait(false))
                {
                    if (_stallAfterAccept)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, _shutdown.Token).ConfigureAwait(false);
                        return;
                    }

                    var seedRequest = await ReadExpectedAsync(stream, ExchangeRandomSeeds.RequestId)
                        .ConfigureAwait(false);
                    if (seedRequest.Bytes.Length != 28)
                    {
                        throw new InvalidDataException("The exchange-random-seeds request has an invalid length.");
                    }

                    await WriteAsync(stream, CreateSeedReply()).ConfigureAwait(false);

                    var startRequest = await ReadExpectedAsync(stream, StartServer.RequestId).ConfigureAwait(false);
                    PasswordIndicator = startRequest.Bytes[20];
                    await WriteAsync(stream, CreateStartReply()).ConfigureAwait(false);
                    if (_startReturnCode != 0)
                    {
                        return;
                    }

                    var attributesRequest = await ReadExpectedAsync(
                            stream,
                            DatabaseProtocol.SetAttributesRequestId)
                        .ConfigureAwait(false);
                    var lobThreshold = ClientAccessCodePoints.Find(attributesRequest.Bytes, 40, 0x3822);
                    LobThreshold = lobThreshold is { } threshold
                        ? BinaryPrimitives.ReadInt32BigEndian(threshold.Span)
                        : null;
                    var closeOnEof = ClientAccessCodePoints.Find(attributesRequest.Bytes, 40, 0x383F);
                    CloseOnEof = closeOnEof is { } close ? close.Span[0] : null;
                    await WriteAsync(
                            stream,
                            CreateDatabaseAttributesReply(
                                attributesRequest.Header.CorrelationId,
                                connectionNumber))
                        .ConfigureAwait(false);

                    if (_expectDefaultCollection)
                    {
                        var collectionRequest = await ReadExpectedAsync(
                                stream,
                                DatabaseProtocol.SetAttributesRequestId)
                            .ConfigureAwait(false);
                        var collection = ClientAccessCodePoints.Find(
                            collectionRequest.Bytes,
                            40,
                            0x380F);
                        if (collection is not { } payload || payload.Length < 4)
                        {
                            throw new InvalidDataException("The default-collection attribute is missing.");
                        }

                        var ccsid = BinaryPrimitives.ReadUInt16BigEndian(payload.Span);
                        var length = BinaryPrimitives.ReadUInt16BigEndian(payload.Span[2..]);
                        if (length > payload.Length - 4)
                        {
                            throw new InvalidDataException("The default-collection attribute is truncated.");
                        }

                        RequestedDefaultCollection = IbmIEncoding.Decode(
                            ccsid,
                            payload.Span.Slice(4, length));
                        await WriteAsync(
                                stream,
                                CreateDatabaseSuccessReply(collectionRequest.Header.CorrelationId))
                            .ConfigureAwait(false);
                    }

                    if (_queryScenario is not null || _queryScenarios is { Count: > 0 })
                    {
                        await RunSqlAsync(
                                stream,
                                _queryScenario ?? _queryScenarios!.Values.First())
                            .ConfigureAwait(false);
                        return;
                    }

                    var buffer = new byte[1];
                    try
                    {
                        _ = await stream.ReadAsync(buffer, _shutdown.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                    }
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnectionCount);
            Interlocked.Increment(ref _closedConnectionCount);
        }
    }

    private async Task RunSqlAsync(Stream stream, FakeQueryScenario fallbackScenario)
    {
        var blockIndexByHandle = new Dictionary<ushort, int>();
        var scenarioByHandle = new Dictionary<ushort, FakeQueryScenario>();
        var sqlByHandle = new Dictionary<ushort, string>();
        var lobs = new Dictionary<int, FakeLobData>();
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var packet = await ClientAccessPacketCodec
                    .ReadAsync(stream, _shutdown.Token)
                    .ConfigureAwait(false);
                _sqlRequests.Enqueue(packet.Header.RequestReplyId);
                var handle = BinaryPrimitives.ReadUInt16BigEndian(packet.Bytes.AsSpan(34));
                var scenario = scenarioByHandle.GetValueOrDefault(handle, fallbackScenario);
                switch (packet.Header.RequestReplyId)
                {
                    case SqlProtocol.CreateRpb:
                        ValidateRpb(packet, handle);
                        break;
                    case SqlProtocol.PrepareDescribe:
                        PreparedSql = DecodeExtendedString(packet, 0x3831);
                        PreparedStatementType = ReadInt16CodePoint(packet, 0x3812);
                        _preparedStatements.Enqueue(PreparedSql);
                        sqlByHandle[handle] = PreparedSql;
                        scenario = fallbackScenario;
                        if (_queryScenarios?.TryGetValue(PreparedSql, out var sqlScenario) == true)
                        {
                            scenario = sqlScenario;
                        }

                        scenarioByHandle[handle] = scenario;
                        if (scenario.Lobs is { } outputLobs)
                        {
                            foreach (var lob in outputLobs)
                            {
                                lobs[lob.Key] = lob.Value;
                            }
                        }

                        foreach (var field in scenario.ParameterFormat.Fields)
                        {
                            if (field.NativeType is 960 or 964 or 968)
                            {
                                lobs.TryAdd(field.LobLocator, new FakeLobData(
                                    [], field.Ccsid, field.NativeType == 968));
                            }
                        }
                        if (scenario.StallOnPrepare)
                        {
                            await _cancelReceived.Task.WaitAsync(_shutdown.Token)
                                .ConfigureAwait(false);
                        }

                        await WriteAsync(
                                stream,
                                scenario.PrepareSqlCode is { } sqlCode
                                    ? CreateSqlReply(
                                        packet.Header.CorrelationId,
                                        SqlProtocol.PrepareDescribe,
                                        errorClass: 1,
                                        returnCode: sqlCode,
                                        sqlCode,
                                        scenario.PrepareSqlState,
                                        scenario.ErrorMessage)
                                    : CreatePrepareReply(
                                        packet.Header.CorrelationId,
                                        scenario))
                            .ConfigureAwait(false);
                        break;
                    case SqlProtocol.Execute:
                        LastParameterData = ClientAccessCodePoints.Find(
                            packet.Bytes,
                            40,
                            SqlProtocol.ExtendedParameterMarkerData);
                        _executedSql.Enqueue(sqlByHandle[handle]);
                        _executedParameters.Enqueue(LastParameterData);
                        CaptureInputLobs(scenario, lobs);
                        if (scenario.StallOnExecute)
                        {
                            await _cancelReceived.Task.WaitAsync(_shutdown.Token)
                                .ConfigureAwait(false);
                        }

                        await WriteAsync(
                                stream,
                                scenario.ExecuteSqlCode is { } executeSqlCode
                                    ? CreateSqlReply(
                                        packet.Header.CorrelationId,
                                        SqlProtocol.Execute,
                                        errorClass: 1,
                                        returnCode: executeSqlCode,
                                        executeSqlCode,
                                        scenario.ExecuteSqlState,
                                        scenario.ErrorMessage)
                                    : CreateSqlReply(
                                        packet.Header.CorrelationId,
                                        SqlProtocol.Execute,
                                        codePoints:
                                        [
                                            (0x3807, CreateSqlca(
                                                0,
                                                "00000",
                                                scenario.RowsAffected)),
                                        ]))
                            .ConfigureAwait(false);
                        break;
                    case DatabaseProtocol.SetAttributesRequestId:
                        var autoCommit = ReadByteCodePoint(packet, 0x3824) == 0xE8;
                        var commitmentLevel = ReadInt16CodePoint(packet, 0x380E);
                        var locatorPayload = ClientAccessCodePoints.Find(
                            packet.Bytes,
                            40,
                            0x3830);
                        var locatorPersistence = locatorPayload is null
                            ? (short?)null
                            : BinaryPrimitives.ReadInt16BigEndian(locatorPayload.Value.Span);
                        _transactionModes.Add((autoCommit, commitmentLevel));
                        _transactionLocatorPersistence.Add(locatorPersistence);
                        var rejectLocatorPersistence = scenario.RejectFirstLocatorPersistenceChange
                            && locatorPersistence.HasValue
                            && !_locatorPersistenceRejected;
                        _locatorPersistenceRejected |= rejectLocatorPersistence;
                        await WriteAsync(
                                stream,
                                CreateDatabaseSuccessReply(
                                    packet.Header.CorrelationId,
                                    errorClass: rejectLocatorPersistence ? (short)7 : (short)0,
                                    returnCode: rejectLocatorPersistence ? -601 : 0))
                            .ConfigureAwait(false);
                        break;
                    case SqlProtocol.Commit:
                    case SqlProtocol.Rollback:
                        var boundarySqlCode = packet.Header.RequestReplyId == SqlProtocol.Commit
                            ? scenario.CommitSqlCode
                            : null;
                        await WriteAsync(
                                stream,
                                boundarySqlCode is { } transactionSqlCode
                                    ? CreateSqlReply(
                                        packet.Header.CorrelationId,
                                        packet.Header.RequestReplyId,
                                        errorClass: 1,
                                        returnCode: transactionSqlCode,
                                        sqlCode: transactionSqlCode,
                                        sqlState: scenario.CommitSqlState,
                                        errorMessage: scenario.ErrorMessage)
                                    : CreateSqlReply(
                                        packet.Header.CorrelationId,
                                        packet.Header.RequestReplyId))
                            .ConfigureAwait(false);
                        break;
                    case SqlProtocol.ChangeDescriptor:
                        LastClientParameterFormat = ParseClientDescriptor(packet);
                        break;
                    case SqlProtocol.OpenDescribeFetch:
                        LastParameterData = ClientAccessCodePoints.Find(
                            packet.Bytes,
                            40,
                            SqlProtocol.ExtendedParameterMarkerData);
                        _executedSql.Enqueue(sqlByHandle[handle]);
                        _executedParameters.Enqueue(LastParameterData);
                        CaptureInputLobs(scenario, lobs);
                        blockIndexByHandle[handle] = 0;
                        if (scenario.StallOnOpen)
                        {
                            await _cancelReceived.Task.WaitAsync(_shutdown.Token)
                                .ConfigureAwait(false);
                        }

                        await WriteAsync(
                                stream,
                                CreateBlockReply(
                                    packet.Header.CorrelationId,
                                    SqlProtocol.OpenDescribeFetch,
                                    scenario,
                                    blockIndex: 0))
                            .ConfigureAwait(false);
                        break;
                    case SqlProtocol.Fetch:
                        FetchCount++;
                        if (scenario.StallOnFetch)
                        {
                            await _cancelReceived.Task.WaitAsync(_shutdown.Token)
                                .ConfigureAwait(false);
                        }

                        var nextBlock = blockIndexByHandle.GetValueOrDefault(handle) + 1;
                        blockIndexByHandle[handle] = nextBlock;
                        await WriteAsync(
                                stream,
                                CreateBlockReply(
                                    packet.Header.CorrelationId,
                                    SqlProtocol.Fetch,
                                    scenario,
                                    nextBlock))
                            .ConfigureAwait(false);
                        break;
                    case SqlProtocol.Cancel:
                        CancelTargetJobIdentifier = DecodeVariableString(packet, 0x3826);
                        _cancelReceived.TrySetResult();
                        await WriteAsync(
                                stream,
                                CreateSqlReply(
                                    packet.Header.CorrelationId,
                                    SqlProtocol.Cancel,
                                    errorClass: scenario.CancelReturnCode == 0
                                        ? (short)0
                                        : (short)1,
                                    returnCode: scenario.CancelReturnCode))
                            .ConfigureAwait(false);
                        break;
                    case 0x1816:
                    case 0x1817:
                    case 0x1819:
                        await HandleLobAsync(stream, packet, scenario, lobs).ConfigureAwait(false);
                        break;
                    case SqlProtocol.Close:
                    case SqlProtocol.DeleteDescriptor:
                        break;
                    case SqlProtocol.DeleteResultSet:
                    case SqlProtocol.DeleteRpb:
                        await WriteAsync(
                                stream,
                                CreateSqlReply(
                                    packet.Header.CorrelationId,
                                    packet.Header.RequestReplyId))
                            .ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unexpected SQL request 0x{packet.Header.RequestReplyId:X4}.");
                }
            }
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void CaptureInputLobs(FakeQueryScenario scenario, Dictionary<int, FakeLobData> lobs)
    {
        var values = new Dictionary<int, byte[]>();
        foreach (var field in scenario.ParameterFormat.Fields)
        {
            if (field.NativeType is 960 or 964 or 968
                && lobs.TryGetValue(field.LobLocator, out var lob))
            {
                values[field.LobLocator] = lob.Data.ToArray();
            }
        }

        _executedLobs.Enqueue(values);
    }

    // The fake implements the JTOpen DBSQLRequestDS / JDLobLocator wire shapes
    // independently of the provider's request and response codecs.
    private async Task HandleLobAsync(
        Stream stream,
        ClientAccessPacket packet,
        FakeQueryScenario scenario,
        Dictionary<int, FakeLobData> lobs)
    {
        var function = packet.Header.RequestReplyId;
        var locator = ReadInt32CodePoint(packet, 0x3818);
        if (function == 0x1819)
        {
            _freedLobHandles.Enqueue(locator);
            if (scenario.LobFreeSqlCode is { } freeSqlCode)
            {
                await WriteAsync(stream, CreateSqlReply(packet.Header.CorrelationId,
                    function, 1, freeSqlCode, freeSqlCode, "HY000", "Fake LOB release failure."))
                    .ConfigureAwait(false);
                return;
            }

            lobs.Remove(locator);
            await WriteAsync(stream, CreateSqlReply(packet.Header.CorrelationId, function))
                .ConfigureAwait(false);
            return;
        }

        if (!lobs.TryGetValue(locator, out var lob))
        {
            throw new InvalidDataException($"Unknown fake LOB locator {locator}.");
        }

        var offset = ReadInt32CodePoint(packet, 0x381A);
        var amount = ReadInt32CodePoint(packet, 0x3819);
        var unitSize = lob.Graphic ? 2 : 1;
        if (offset < 0 || amount < 0)
        {
            throw new InvalidDataException("Negative LOB request offset or amount.");
        }

        if (function == 0x1816)
        {
            _lobReads.Enqueue(new FakeLobRequest(locator, offset, amount, false));
            if (scenario.StallOnLobRead)
            {
                await _cancelReceived.Task.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }

            if (scenario.LobReadSqlCode is { } sqlCode)
            {
                await WriteAsync(stream, CreateSqlReply(packet.Header.CorrelationId,
                    function, 1, sqlCode, sqlCode, "HY000", "Fake LOB retrieval failure."))
                    .ConfigureAwait(false);
                return;
            }

            var byteOffset = checked(offset * unitSize);
            var count = Math.Min(checked(amount * unitSize), Math.Max(0, lob.Data.Length - byteOffset));
            var payload = new byte[6 + count];
            BinaryPrimitives.WriteUInt16BigEndian(payload, checked((ushort)lob.Ccsid));
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(2), count / unitSize);
            lob.Data.AsSpan(Math.Min(byteOffset, lob.Data.Length), count).CopyTo(payload.AsSpan(6));
            var logicalLength = lob.LogicalLength ?? lob.Data.Length / unitSize;
            var length = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(length, 8);
            BinaryPrimitives.WriteInt64BigEndian(length.AsSpan(2), logicalLength);
            await WriteAsync(stream, CreateSqlReply(packet.Header.CorrelationId, function,
                codePoints: [(0x380F, payload), (0x3810, length)]))
                .ConfigureAwait(false);
            return;
        }

        var truncatePayload = ClientAccessCodePoints.Find(packet.Bytes, 40, 0x3822);
        var truncate = truncatePayload is { } trunc && trunc.Span[0] == 0xF0;
        _lobWrites.Enqueue(new FakeLobRequest(locator, offset, amount, truncate));
        if (scenario.StallOnLobWrite)
        {
            await _cancelReceived.Task.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        }

        if (scenario.LobWriteSqlCode is { } writeSqlCode)
        {
            await WriteAsync(stream, CreateSqlReply(packet.Header.CorrelationId,
                function, 1, writeSqlCode, writeSqlCode, "HY000", "Fake LOB write failure."))
                .ConfigureAwait(false);
            return;
        }

        var data = ClientAccessCodePoints.Find(packet.Bytes, 40, 0x381D)
            ?? throw new InvalidDataException("LOB write data is missing.");
        if (data.Length < 6 || BinaryPrimitives.ReadUInt16BigEndian(data.Span) != 65535)
        {
            throw new InvalidDataException("Unexpected LOB write CCSID or truncated data.");
        }

        var byteCount = BinaryPrimitives.ReadInt32BigEndian(data.Span[2..]);
        if (byteCount != data.Length - 6 || byteCount != checked(amount * unitSize))
        {
            throw new InvalidDataException("LOB write amount differs from the data length.");
        }

        var destinationOffset = checked(offset * unitSize);
        var newLength = checked(destinationOffset + byteCount);
        var result = new byte[truncate ? newLength : Math.Max(lob.Data.Length, newLength)];
        lob.Data.AsSpan(0, Math.Min(lob.Data.Length, result.Length)).CopyTo(result);
        data.Span[6..].CopyTo(result.AsSpan(destinationOffset));
        lobs[locator] = lob with { Data = result, LogicalLength = null };
        _uploadedLobs[locator] = result;
        await WriteAsync(stream, CreateSqlReply(packet.Header.CorrelationId, function))
            .ConfigureAwait(false);
    }

    private static int ReadInt32CodePoint(ClientAccessPacket packet, ushort codePoint)
    {
        var payload = ClientAccessCodePoints.Find(packet.Bytes, 40, codePoint)
            ?? throw new InvalidDataException($"Code point 0x{codePoint:X4} is missing.");
        if (payload.Length != 4)
        {
            throw new InvalidDataException($"Code point 0x{codePoint:X4} is not an Int32.");
        }

        return BinaryPrimitives.ReadInt32BigEndian(payload.Span);
    }

    private byte[] CreateSeedReply()
    {
        var bytes = new byte[32];
        new ClientAccessHeader(
                bytes.Length,
                HeaderId: _passwordLevel,
                ClientAccessHeader.SqlServerId,
                ClientServerInstance: 0,
                CorrelationId: 0,
                TemplateLength: 12,
                ExchangeRandomSeeds.ReplyId)
            .WriteTo(bytes);
        ServerSeed.CopyTo(bytes, 24);
        return bytes;
    }

    private byte[] CreateStartReply()
    {
        var bytes = new byte[24];
        new ClientAccessHeader(
                bytes.Length,
                HeaderId: 0,
                ClientAccessHeader.SqlServerId,
                ClientServerInstance: 0,
                CorrelationId: 0,
                TemplateLength: 4,
                StartServer.ReplyId)
            .WriteTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), _startReturnCode);
        return bytes;
    }

    private static byte[] CreateDatabaseAttributesReply(int correlationId, int connectionNumber)
    {
        var body = new byte[116];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(19), 37);
        WriteEbcdic(body, 50, 10, "V7R3M00");
        WriteEbcdic(body, 60, 18, "MYRDB");
        WriteEbcdic(body, 78, 10, "MYLIB");
        WriteEbcdic(body, 88, 10, "QZDASOINIT");
        WriteEbcdic(body, 98, 10, "TESTUSER");
        WriteEbcdic(body, 108, 6, (123455 + connectionNumber).ToString("D6"));

        var codePointLength = 8 + body.Length;
        var packet = CreateDatabaseSuccessReply(correlationId, codePointLength);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(40), codePointLength);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(44), 0x3804);
        body.CopyTo(packet, 48);
        return packet;
    }

    private static byte[] CreateDatabaseSuccessReply(
        int correlationId,
        int extraLength = 0,
        short errorClass = 0,
        int returnCode = 0)
    {
        var packet = new byte[40 + extraLength];
        new ClientAccessHeader(
                packet.Length,
                HeaderId: 0,
                ClientAccessHeader.SqlServerId,
                ClientServerInstance: 0,
                CorrelationId: correlationId,
                TemplateLength: 20,
                DatabaseProtocol.ReplyId)
            .WriteTo(packet);
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(30),
            DatabaseProtocol.SetAttributesRequestId);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(34), errorClass);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(36), returnCode);
        return packet;
    }

    private static byte[] CreatePrepareReply(
        int correlationId,
        FakeQueryScenario scenario)
    {
        var parameterDescriptor = EncodeDescriptor(scenario.ParameterFormat);
        var codePoints = new List<(ushort CodePoint, byte[] Payload)>();
        if (scenario.ResultFormat is { } resultFormat)
        {
            codePoints.Add((SqlProtocol.ExtendedDataFormat, EncodeDescriptor(resultFormat)));
        }

        codePoints.Add((SqlProtocol.ExtendedParameterMarkerFormat, parameterDescriptor));
        codePoints.Add((0x3807, CreateSqlca(0, "00000")));
        return CreateSqlReply(
            correlationId,
            SqlProtocol.PrepareDescribe,
            codePoints: codePoints);
    }

    private static byte[] CreateBlockReply(
        int correlationId,
        ushort functionId,
        FakeQueryScenario scenario,
        int blockIndex)
    {
        var resultFormat = scenario.ResultFormat
            ?? throw new InvalidOperationException(
                "A fake query block requires a result descriptor.");
        IReadOnlyList<IReadOnlyList<object?[]>> blocks = scenario.Blocks.Count == 0
            ? [Array.Empty<object?[]>()]
            : scenario.Blocks;
        var pastEnd = blockIndex >= blocks.Count;
        IReadOnlyList<object?[]> rows = pastEnd
            ? Array.Empty<object?[]>()
            : blocks[blockIndex];
        var isLast = pastEnd || blockIndex == blocks.Count - 1;
        var data = scenario.ReturnEmptyResultPayload && rows.Count == 0
            ? []
            : EncodeRows(resultFormat, rows);
        return CreateSqlReply(
            correlationId,
            functionId,
            errorClass: isLast ? (short)1 : (short)0,
            returnCode: isLast ? 100 : 0,
            sqlCode: isLast ? 100 : 0,
            sqlState: isLast ? "02000" : "00000",
            codePoints:
            [
                (SqlProtocol.ExtendedResultData, data),
            ]);
    }

    private static byte[] CreateSqlReply(
        int correlationId,
        ushort functionId,
        short errorClass = 0,
        int returnCode = 0,
        int sqlCode = 0,
        string sqlState = "00000",
        string? errorMessage = null,
        IReadOnlyList<(ushort CodePoint, byte[] Payload)>? codePoints = null)
    {
        var values = new List<(ushort CodePoint, byte[] Payload)>();
        if (codePoints is not null)
        {
            values.AddRange(codePoints);
        }

        if (!values.Any(value => value.CodePoint == 0x3807))
        {
            values.Add((0x3807, CreateSqlca(sqlCode, sqlState)));
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            var text = IbmIEncoding.Encode(37, errorMessage);
            var payload = new byte[4 + text.Length];
            BinaryPrimitives.WriteUInt16BigEndian(payload, 37);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), (ushort)text.Length);
            text.CopyTo(payload, 4);
            values.Add((0x3802, payload));
        }

        var extraLength = values.Sum(value => 6 + value.Payload.Length);
        var packet = new byte[40 + extraLength];
        new ClientAccessHeader(
                packet.Length,
                HeaderId: 0,
                ClientAccessHeader.SqlServerId,
                ClientServerInstance: 0,
                CorrelationId: correlationId,
                TemplateLength: 20,
                DatabaseProtocol.ReplyId)
            .WriteTo(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(30), functionId);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(34), errorClass);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(36), returnCode);

        var offset = 40;
        foreach (var value in values)
        {
            var length = 6 + value.Payload.Length;
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(offset), length);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 4), value.CodePoint);
            value.Payload.CopyTo(packet, offset + 6);
            offset += length;
        }

        return packet;
    }

    private static byte[] CreateSqlca(
        int sqlCode,
        string sqlState,
        int rowsAffected = 0)
    {
        var payload = new byte[136];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12), sqlCode);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(104), rowsAffected);
        IbmIEncoding.Encode(37, sqlState).CopyTo(payload, 131);
        return payload;
    }

    private static byte[] EncodeDescriptor(Db2iDataFormat format)
    {
        var payload = new byte[16 + (format.Fields.Count * 64)];
        BinaryPrimitives.WriteInt32BigEndian(payload, format.ConsistencyToken);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), format.Fields.Count);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12), format.RecordSize);
        for (var index = 0; index < format.Fields.Count; index++)
        {
            var source = format.Fields[index];
            var field = payload.AsSpan(16 + (index * 64), 64);
            BinaryPrimitives.WriteUInt16BigEndian(field, 64);
            BinaryPrimitives.WriteUInt16BigEndian(field[2..], (ushort)source.SqlType);
            BinaryPrimitives.WriteInt32BigEndian(field[4..], source.Length);
            BinaryPrimitives.WriteInt16BigEndian(field[8..], (short)source.Scale);
            BinaryPrimitives.WriteInt16BigEndian(field[10..], (short)source.Precision);
            BinaryPrimitives.WriteUInt16BigEndian(field[12..], (ushort)source.Ccsid);
            BinaryPrimitives.WriteInt32BigEndian(field[17..], source.LobLocator);
            BinaryPrimitives.WriteInt32BigEndian(field[26..], source.LobMaxSize);
            var name = IbmIEncoding.Encode(37, source.Name);
            if (name.Length > 30)
            {
                throw new ArgumentException("Fake column names cannot exceed 30 bytes.");
            }

            BinaryPrimitives.WriteUInt16BigEndian(field[30..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16BigEndian(field[32..], 37);
            name.CopyTo(field[34..]);
        }

        return payload;
    }

    private static byte[] EncodeRows(
        Db2iDataFormat format,
        IReadOnlyList<object?[]> rows)
    {
        var parameters = new Db2iParameter[format.Fields.Count];
        var rowPayloads = new List<byte[]>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Length != parameters.Length)
            {
                throw new ArgumentException("Fake row and descriptor column counts differ.");
            }

            for (var index = 0; index < row.Length; index++)
            {
                parameters[index] = new Db2iParameter { Value = row[index] };
            }

            // Locator output rows contain their actual handles, independently of
            // input binding (which obtains handles from parameter descriptors).
            var scalarFields = format.Fields.Select(field => field.NativeType is 960 or 964 or 968
                ? field with { SqlType = 497, Ccsid = 0 } : field).ToArray();
            rowPayloads.Add(Db2iTypeCodec.EncodeParameterRow(
                format with { Fields = scalarFields }, parameters));
        }

        var indicatorLength = rows.Count * format.Fields.Count * 2;
        var payload = new byte[20 + indicatorLength + (rows.Count * format.RecordSize)];
        BinaryPrimitives.WriteInt32BigEndian(payload, format.ConsistencyToken);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), rows.Count);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8), (ushort)format.Fields.Count);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10), 2);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(16), format.RecordSize);

        for (var rowIndex = 0; rowIndex < rowPayloads.Count; rowIndex++)
        {
            var source = rowPayloads[rowIndex];
            var sourceIndicators = source.AsSpan(20, format.Fields.Count * 2);
            sourceIndicators.CopyTo(
                payload.AsSpan(20 + (rowIndex * format.Fields.Count * 2)));
            source.AsSpan(20 + (format.Fields.Count * 2), format.RecordSize)
                .CopyTo(
                    payload.AsSpan(
                        20 + indicatorLength + (rowIndex * format.RecordSize)));
        }

        return payload;
    }

    private static Db2iDataFormat ParseClientDescriptor(ClientAccessPacket packet)
    {
        var descriptor = ClientAccessCodePoints.Find(packet.Bytes, 40, 0x381E)
            ?? throw new InvalidDataException("The client descriptor is missing.");
        var span = descriptor.Span;
        var count = BinaryPrimitives.ReadInt32BigEndian(span[4..]);
        var fields = new List<Db2iFieldDescriptor>(count);
        var offset = 0;
        for (var index = 0; index < count; index++)
        {
            var field = span.Slice(16 + (index * 64), 64);
            var length = BinaryPrimitives.ReadInt32BigEndian(field[4..]);
            fields.Add(new Db2iFieldDescriptor(
                BinaryPrimitives.ReadUInt16BigEndian(field[2..]),
                length,
                BinaryPrimitives.ReadInt16BigEndian(field[8..]),
                BinaryPrimitives.ReadInt16BigEndian(field[10..]),
                BinaryPrimitives.ReadUInt16BigEndian(field[12..]),
                string.Empty,
                offset));
            offset += length;
        }

        return new Db2iDataFormat(
            BinaryPrimitives.ReadInt32BigEndian(span),
            BinaryPrimitives.ReadInt32BigEndian(span[12..]),
            5,
            2,
            fields);
    }

    private static string DecodeExtendedString(ClientAccessPacket packet, ushort codePoint)
    {
        var payload = ClientAccessCodePoints.Find(packet.Bytes, 40, codePoint)
            ?? throw new InvalidDataException($"Code point 0x{codePoint:X4} is missing.");
        var span = payload.Span;
        if (span.Length < 6)
        {
            throw new InvalidDataException("The extended string is truncated.");
        }

        var ccsid = BinaryPrimitives.ReadUInt16BigEndian(span);
        var length = BinaryPrimitives.ReadInt32BigEndian(span[2..]);
        if (length < 0 || length > span.Length - 6)
        {
            throw new InvalidDataException("The extended string length is invalid.");
        }

        return IbmIEncoding.DecodeExact(ccsid, span.Slice(6, length));
    }

    private static string DecodeVariableString(ClientAccessPacket packet, ushort codePoint)
    {
        var payload = ClientAccessCodePoints.Find(packet.Bytes, 40, codePoint)
            ?? throw new InvalidDataException($"Code point 0x{codePoint:X4} is missing.");
        var span = payload.Span;
        if (span.Length < 4)
        {
            throw new InvalidDataException("The variable string is truncated.");
        }

        var ccsid = BinaryPrimitives.ReadUInt16BigEndian(span);
        var length = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        if (length > span.Length - 4)
        {
            throw new InvalidDataException("The variable string length is invalid.");
        }

        return IbmIEncoding.DecodeExact(ccsid, span.Slice(4, length));
    }

    private static short ReadInt16CodePoint(ClientAccessPacket packet, ushort codePoint)
    {
        var payload = ClientAccessCodePoints.Find(packet.Bytes, 40, codePoint)
            ?? throw new InvalidDataException($"Code point 0x{codePoint:X4} is missing.");
        if (payload.Length != 2)
        {
            throw new InvalidDataException($"Code point 0x{codePoint:X4} is not an Int16.");
        }

        return BinaryPrimitives.ReadInt16BigEndian(payload.Span);
    }

    private static byte ReadByteCodePoint(ClientAccessPacket packet, ushort codePoint)
    {
        var payload = ClientAccessCodePoints.Find(packet.Bytes, 40, codePoint)
            ?? throw new InvalidDataException($"Code point 0x{codePoint:X4} is missing.");
        if (payload.Length != 1)
        {
            throw new InvalidDataException($"Code point 0x{codePoint:X4} is not a byte.");
        }

        return payload.Span[0];
    }

    private static void ValidateRpb(ClientAccessPacket packet, ushort handle)
    {
        if (handle == 0
            || BinaryPrimitives.ReadUInt16BigEndian(packet.Bytes.AsSpan(28)) != handle
            || packet.Header.CorrelationId != handle)
        {
            throw new InvalidDataException("The RPB handles are inconsistent.");
        }
    }

    private static async ValueTask<ClientAccessPacket> ReadExpectedAsync(
        Stream stream,
        ushort requestId)
    {
        var packet = await ClientAccessPacketCodec.ReadAsync(stream, CancellationToken.None)
            .ConfigureAwait(false);
        if (packet.Header.RequestReplyId != requestId)
        {
            throw new InvalidDataException(
                $"Expected request 0x{requestId:X4}, received 0x{packet.Header.RequestReplyId:X4}.");
        }

        return packet;
    }

    private static ValueTask WriteAsync(Stream stream, byte[] packet)
        => ClientAccessPacketCodec.WriteAsync(stream, packet, CancellationToken.None);

    private void UpdateMaximumActiveConnections(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maximumActiveConnectionCount);
            if (active <= current
                || Interlocked.CompareExchange(
                    ref _maximumActiveConnectionCount,
                    active,
                    current) == current)
            {
                return;
            }
        }
    }

    private static void WriteEbcdic(byte[] destination, int offset, int length, string value)
    {
        var encoded = IbmIEncoding.Encode(37, value.PadRight(length, ' '));
        encoded.CopyTo(destination, offset);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
}

internal sealed record FakeQueryScenario(
    Db2iDataFormat? ResultFormat,
    Db2iDataFormat ParameterFormat,
    IReadOnlyList<IReadOnlyList<object?[]>> Blocks,
    int? PrepareSqlCode = null,
    string PrepareSqlState = "00000",
    string? ErrorMessage = null,
    bool StallOnPrepare = false,
    bool StallOnOpen = false,
    bool StallOnFetch = false,
    int RowsAffected = 0,
    int? ExecuteSqlCode = null,
    string ExecuteSqlState = "00000",
    bool StallOnExecute = false,
    bool ReturnEmptyResultPayload = false,
    int CancelReturnCode = 0,
    int? CommitSqlCode = null,
    string CommitSqlState = "00000",
    bool RejectFirstLocatorPersistenceChange = false,
    IReadOnlyDictionary<int, FakeLobData>? Lobs = null,
    bool StallOnLobRead = false,
    bool StallOnLobWrite = false,
    int? LobReadSqlCode = null,
    int? LobWriteSqlCode = null,
    int? LobFreeSqlCode = null);

internal sealed record FakeLobData(
    byte[] Data,
    int Ccsid = 65535,
    bool Graphic = false,
    long? LogicalLength = null);

internal sealed record FakeLobRequest(int Handle, int Offset, int Amount, bool Truncate);
