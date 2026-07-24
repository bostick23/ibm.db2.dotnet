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
        var request = ExchangeRandomSeeds.CreateRequest(new byte[8]);
        await using var stream = new MemoryStream(request.Bytes);

        var packet = await ClientAccessPacketCodec.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(request.Bytes, packet.Bytes);
        Assert.Equal(8, packet.Payload.Length);
    }
}
