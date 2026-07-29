using System.Buffers.Binary;
using Db2i.Protocol;

namespace Db2i.Tests;

public sealed class ClientAccessProtocolTests
{
    [Fact]
    public void HeaderRoundTripsInNetworkByteOrder()
    {
        var expected = new ClientAccessHeader(
            Length: 0x01020304,
            HeaderId: 0x0506,
            ServerId: ClientAccessHeader.SqlServerId,
            ClientServerInstance: 0x0708090A,
            CorrelationId: 0x0B0C0D0E,
            TemplateLength: 0x0F10,
            RequestReplyId: 0x1812);
        var bytes = new byte[ClientAccessHeader.Size];

        expected.WriteTo(bytes);
        var actual = ClientAccessHeader.Parse(bytes);

        Assert.Equal(expected, actual);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes[..4]);
        Assert.Equal(new byte[] { 0xE0, 0x04 }, bytes[6..8]);
    }

    [Fact]
    public void ExchangeRandomSeedsRequestMatchesTheJtOpenWireLayout()
    {
        byte[] seed = [1, 2, 3, 4, 5, 6, 7, 8];

        var request = ExchangeRandomSeeds.CreateRequest(seed);
        var header = ClientAccessHeader.Parse(request.Bytes);

        Assert.Equal(28, header.Length);
        Assert.Equal(0x0300, header.HeaderId);
        Assert.Equal(ClientAccessHeader.SqlServerId, header.ServerId);
        Assert.Equal(8, header.TemplateLength);
        Assert.Equal(ExchangeRandomSeeds.RequestId, header.RequestReplyId);
        Assert.Equal(seed, request.Bytes[20..28]);
    }

    [Fact]
    public void ParsesExchangeRandomSeedsReply()
    {
        var bytes = new byte[39];
        new ClientAccessHeader(
                bytes.Length,
                HeaderId: 0x0003,
                ClientAccessHeader.SqlServerId,
                ClientServerInstance: 0,
                CorrelationId: 0,
                TemplateLength: 12,
                ExchangeRandomSeeds.ReplyId)
            .WriteTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), 0);
        new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }.CopyTo(bytes, 24);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(32, 4), 7);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36, 2), 0x112E);
        bytes[38] = 1;

        var reply = ExchangeRandomSeeds.ParseReply(new ClientAccessPacket(bytes));

        Assert.Equal(0, reply.ReturnCode);
        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, reply.ServerSeed);
        Assert.Equal(3, reply.PasswordLevel);
        Assert.True(reply.AcceptsAdditionalAuthenticationFactor);
    }

    [Fact]
    public async Task PacketCodecReadsHeaderAndPayload()
    {
        var request = ExchangeRandomSeeds.CreateRequest(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
        await using var stream = new MemoryStream(request.Bytes);

        var packet = await ClientAccessPacketCodec.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(request.Bytes, packet.Bytes);
        Assert.Equal(8, packet.Payload.Length);
    }

    [Fact]
    public void ExchangeRandomSeedsRejectsValuesRejectedByIBM_i()
    {
        Assert.Throws<ArgumentException>(() => ExchangeRandomSeeds.CreateRequest(new byte[8]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExchangeRandomSeeds.CreateRequest(new byte[] { 0xE0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void StartServerRequestMatchesTheJtOpenWireLayout()
    {
        var user = SignonEncoding.EncodeProfile("TESTUSER");
        var substitute = Convert.FromHexString("296AF9F03F274B8C");

        var request = StartServer.CreateRequest(user, substitute);
        var header = ClientAccessHeader.Parse(request);

        Assert.Equal(52, header.Length);
        Assert.Equal(0x0200, header.HeaderId);
        Assert.Equal(ClientAccessHeader.SqlServerId, header.ServerId);
        Assert.Equal(2, header.TemplateLength);
        Assert.Equal(StartServer.RequestId, header.RequestReplyId);
        Assert.Equal(0x01, request[20]);
        Assert.Equal(0x01, request[21]);
        Assert.Equal(14, BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(22, 4)));
        Assert.Equal(0x1105, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(26, 2)));
        Assert.Equal(substitute, request[28..36]);
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(36, 4)));
        Assert.Equal(0x1104, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(40, 2)));
        Assert.Equal(user, request[42..52]);
    }

    [Fact]
    public void DatabaseAttributesRequestContainsTheBootstrapContract()
    {
        var settings = new Db2iConnectionStringBuilder(
            "Server=my-system;User ID=TESTUSER;Password=secret;Database=MYRDB")
            .BuildSettings();

        var request = DatabaseProtocol.CreateBootstrapAttributesRequest(settings, 1, "0.2.0");
        var header = ClientAccessHeader.Parse(request);

        Assert.Equal(DatabaseProtocol.SetAttributesRequestId, header.RequestReplyId);
        Assert.Equal(1, header.CorrelationId);
        Assert.Equal(20, header.TemplateLength);
        Assert.Equal(
            DatabaseProtocol.ReturnData | DatabaseProtocol.ServerAttributes,
            BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(20, 4)));
        Assert.Equal(18, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(38, 2)));
        Assert.NotNull(ClientAccessCodePoints.Find(request, 40, 0x3801));
        Assert.NotNull(ClientAccessCodePoints.Find(request, 40, 0x3807));
        Assert.NotNull(ClientAccessCodePoints.Find(request, 40, 0x3809));
        Assert.NotNull(ClientAccessCodePoints.Find(request, 40, 0x3821));
        Assert.Equal(
            [0x00, 0x00],
            ClientAccessCodePoints.Find(request, 40, 0x380E)?.ToArray());
        Assert.Equal(
            [0xE8],
            ClientAccessCodePoints.Find(request, 40, 0x3824)?.ToArray());
        Assert.Equal(
            [0x40, 0x00, 0x00, 0x00],
            ClientAccessCodePoints.Find(request, 40, 0x3825)?.ToArray());
        Assert.NotNull(ClientAccessCodePoints.Find(request, 40, 0x3826));
        Assert.NotNull(ClientAccessCodePoints.Find(request, 40, 0x383D));
    }

    [Fact]
    public void ParsesDatabaseServerAttributes()
    {
        var packet = CreateDatabaseAttributesReply();

        var reply = DatabaseProtocol.ParseReply(new ClientAccessPacket(packet), 1);
        var info = DatabaseProtocol.ParseServerInfo(Assert.IsType<ReadOnlyMemory<byte>>(reply.ServerAttributes));

        Assert.Equal(DatabaseProtocol.SetAttributesRequestId, reply.ReturnFunctionId);
        Assert.Equal(0, reply.ErrorClass);
        Assert.Equal(0, reply.ReturnCode);
        Assert.Equal(new Version(7, 3, 0), info.Version);
        Assert.Equal(37, info.Ccsid);
        Assert.Equal("V7R3M00", info.FunctionalLevel);
        Assert.Equal("MYRDB", info.RelationalDatabaseName);
        Assert.Equal("MYLIB", info.DefaultCollection);
        Assert.Equal("123456/TESTUSER/QZDASOINIT", info.JobIdentifier);
    }

    [Fact]
    public void ResolvesTheItalianIbmICcsid()
    {
        var encoded = IbmIEncoding.Encode(280, "PROVA");

        Assert.Equal("D7D9D6E5C1", Convert.ToHexString(encoded));
        Assert.Equal("PROVA", IbmIEncoding.Decode(280, encoded));
        Assert.Equal(20280, IbmIEncoding.Get(280).CodePage);
    }

    [Fact]
    public void TransactionAndCancelRequestsMatchTheJtOpenContract()
    {
        var attributes = DatabaseProtocol.CreateTransactionAttributesRequest(
            autoCommit: false,
            commitmentControlLevel: 4,
            correlationId: 9);
        var attributesHeader = ClientAccessHeader.Parse(attributes);
        Assert.Equal(DatabaseProtocol.SetAttributesRequestId, attributesHeader.RequestReplyId);
        Assert.Equal(9, attributesHeader.CorrelationId);
        Assert.Equal(
            DatabaseProtocol.ReturnData
                | DatabaseProtocol.ServerAttributes
                | DatabaseProtocol.ReplyRleCompression,
            BinaryPrimitives.ReadInt32BigEndian(attributes.AsSpan(20)));
        Assert.Equal(0x3824, BinaryPrimitives.ReadUInt16BigEndian(attributes.AsSpan(44)));
        Assert.Equal(0x380E, BinaryPrimitives.ReadUInt16BigEndian(attributes.AsSpan(51)));
        Assert.Equal(0x3830, BinaryPrimitives.ReadUInt16BigEndian(attributes.AsSpan(59)));
        Assert.Equal(
            [0x00, 0x04],
            ClientAccessCodePoints.Find(attributes, 40, 0x380E)?.ToArray());
        Assert.Equal(
            [0xD5],
            ClientAccessCodePoints.Find(attributes, 40, 0x3824)?.ToArray());
        Assert.Equal(
            [0x00, 0x01],
            ClientAccessCodePoints.Find(attributes, 40, 0x3830)?.ToArray());

        var attributesWithoutLocator =
            DatabaseProtocol.CreateTransactionAttributesRequest(
                autoCommit: false,
                commitmentControlLevel: 4,
                correlationId: 10,
                includeLocatorPersistence: false);
        Assert.Null(ClientAccessCodePoints.Find(attributesWithoutLocator, 40, 0x3830));
        Assert.Equal(
            2,
            BinaryPrimitives.ReadUInt16BigEndian(attributesWithoutLocator.AsSpan(38)));

        var commit = SqlProtocol.CreateTransactionBoundaryRequest(SqlProtocol.Commit, 10);
        var commitHeader = ClientAccessHeader.Parse(commit);
        Assert.Equal(SqlProtocol.Commit, commitHeader.RequestReplyId);
        Assert.Equal(10, commitHeader.CorrelationId);
        Assert.Equal(
            [0x00],
            ClientAccessCodePoints.Find(commit, 40, 0x380F)?.ToArray());

        var cancel = SqlProtocol.CreateCancelRequest(
            correlationId: 11,
            serverCcsid: 37,
            targetJobIdentifier: "123456/TESTUSER/QZDASOINIT");
        var cancelHeader = ClientAccessHeader.Parse(cancel);
        Assert.Equal(SqlProtocol.Cancel, cancelHeader.RequestReplyId);
        Assert.Equal(11, cancelHeader.CorrelationId);
        var target = Assert.IsType<ReadOnlyMemory<byte>>(
            ClientAccessCodePoints.Find(cancel, 40, 0x3826));
        Assert.Equal(37, BinaryPrimitives.ReadUInt16BigEndian(target.Span));
        var targetLength = BinaryPrimitives.ReadUInt16BigEndian(target.Span[2..]);
        Assert.Equal(
            "123456/TESTUSER/QZDASOINIT",
            IbmIEncoding.DecodeExact(37, target.Span.Slice(4, targetLength)));
    }

    [Fact]
    public void SqlcaDiagnosticReadsSqlerrd3AsAffectedRows()
    {
        var sqlca = new byte[136];
        BinaryPrimitives.WriteInt32BigEndian(sqlca.AsSpan(12), 0);
        BinaryPrimitives.WriteInt32BigEndian(sqlca.AsSpan(104), 27);
        IbmIEncoding.Encode(37, "00000").CopyTo(sqlca, 131);
        var codePointLength = 6 + sqlca.Length;
        var bytes = new byte[40 + codePointLength];
        new ClientAccessHeader(
                bytes.Length,
                HeaderId: 0,
                ClientAccessHeader.SqlServerId,
                ClientServerInstance: 0,
                CorrelationId: 7,
                TemplateLength: 20,
                RequestReplyId: DatabaseProtocol.ReplyId)
            .WriteTo(bytes);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(30), SqlProtocol.Execute);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(40), codePointLength);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(44), 0x3807);
        sqlca.CopyTo(bytes, 46);

        var reply = DatabaseProtocol.ParseReply(new ClientAccessPacket(bytes), 7);
        var diagnostic = SqlProtocol.ParseDiagnostic(reply, 37);

        Assert.Equal(0, diagnostic.SqlCode);
        Assert.Equal("00000", diagnostic.SqlState);
        Assert.Equal(27, diagnostic.RowsAffected);
    }

    private static byte[] CreateDatabaseAttributesReply()
    {
        var body = new byte[116];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(19, 2), 37);
        WriteEbcdic(body, 50, 10, "V7R3M00");
        WriteEbcdic(body, 60, 18, "MYRDB");
        WriteEbcdic(body, 78, 10, "MYLIB");
        WriteEbcdic(body, 88, 10, "QZDASOINIT");
        WriteEbcdic(body, 98, 10, "TESTUSER");
        WriteEbcdic(body, 108, 6, "123456");

        var codePointLength = 6 + 2 + body.Length;
        var packet = new byte[40 + codePointLength];
        new ClientAccessHeader(
                packet.Length,
                HeaderId: 0,
                ClientAccessHeader.SqlServerId,
                ClientServerInstance: 0,
                CorrelationId: 1,
                TemplateLength: 20,
                RequestReplyId: DatabaseProtocol.ReplyId)
            .WriteTo(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(30, 2), DatabaseProtocol.SetAttributesRequestId);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(40, 4), codePointLength);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(44, 2), 0x3804);
        body.CopyTo(packet, 48);
        return packet;
    }

    private static void WriteEbcdic(byte[] destination, int offset, int length, string value)
    {
        var encoded = IbmIEncoding.Encode(37, value.PadRight(length, ' '));
        encoded.CopyTo(destination, offset);
    }
}
