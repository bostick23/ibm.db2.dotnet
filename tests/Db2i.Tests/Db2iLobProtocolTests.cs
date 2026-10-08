using System.Buffers.Binary;
using System.Data;
using Db2i.Protocol;

namespace Db2i.Tests;

public sealed class Db2iLobProtocolTests
{
    // Independent wire vectors from IBM/JTOpen JDLobLocator and DBSQLRequestDS.
    // https://github.com/IBM/JTOpen/blob/main/src/main/java/com/ibm/as400/access/JDLobLocator.java
    // https://github.com/IBM/JTOpen/blob/main/src/main/java/com/ibm/as400/access/DBSQLRequestDS.java
    [Fact]
    public void LocatorNegotiationRetainsEofCursor()
    {
        var settings = new Db2iConnectionStringBuilder("Server=example;User ID=USER;Password=secret").BuildSettings();
        var request = DatabaseProtocol.CreateBootstrapAttributesRequest(settings, 1, "test");
        Assert.Equal(Convert.FromHexString("00000000"), Payload(request, 0x3822));
        Assert.Equal(Convert.FromHexString("D5"), Payload(request, 0x383F));
    }

    [Fact]
    public void ReadRequestMatchesJtOpenWireVector()
    {
        var request = SqlProtocol.CreateReadLobRequest(3, new(Field(960), 0x01020304, 2), 65536, 65536);
        Assert.Equal(0x1816, ClientAccessHeader.Parse(request).RequestReplyId);
        Assert.Equal(Convert.FromHexString(
            "0000000A3818010203040000000A3819000100000000000A381A00010000" +
            "00000007381BF0000000073821F10000000A382800000003"), request[40..]);
        Assert.Equal(unchecked((int)0x84000000), BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(20)));
        // DBBaseRequestDS places the same RPB ID in return/fill ORS and RPB handles.
        Assert.Equal(Convert.FromHexString("8400000000000000000300030000000300000006"), request[20..40]);
    }

    [Fact]
    public void WriteRequestUsesGraphicUnitsAndByteLengthPrefix()
    {
        var request = SqlProtocol.CreateWriteLobRequest(3, Field(968), 2, [0, 65, 0, 66], true);
        Assert.Equal(0x1817, ClientAccessHeader.Parse(request).RequestReplyId);
        Assert.Equal(Convert.FromHexString("8400000000000000000300030000000300000006"), request[20..40]);
        Assert.Equal(Convert.FromHexString(
            "000000073822F00000000A3818010203040000000A381900000002" +
            "0000000A381A0000000200000007381BF000000010381DFFFF0000000400410042"), request[40..]);
    }

    [Fact]
    public void EmptyWriteTruncatesAndFreeCarriesOnlyHandle()
    {
        var request = SqlProtocol.CreateWriteLobRequest(3, Field(960), 0, [], true);
        Assert.Equal(Convert.FromHexString("FFFF00000000"), Payload(request, 0x381D));
        Assert.Equal(new byte[] { 0xF0 }, Payload(request, 0x3822));
        var free = SqlProtocol.CreateFreeLobRequest(3, 0x01020304);
        Assert.Equal(0x1819, ClientAccessHeader.Parse(free).RequestReplyId);
        Assert.Equal(Convert.FromHexString("8000000000000000000300030000000300000001"), free[20..40]);
        Assert.Equal(Convert.FromHexString("0000000A381801020304"), free[40..]);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2147483648L, 1)]
    [InlineData(2147483647L, 1)]
    [InlineData(0, -1)]
    [InlineData(0, 65537)]
    public void RejectsOffsetsAndLengthsOutsideWireLimits(long offset, int length)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlProtocol.CreateReadLobRequest(3, new(Field(960), 1, 0), offset, length));

    [Fact]
    public void GraphicRequestsRejectOddByteCountsAndLimitBytesPerChunk()
    {
        Assert.Throws<ArgumentException>(() => SqlProtocol.CreateWriteLobRequest(3, Field(968), 0, [0], false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlProtocol.CreateReadLobRequest(3, new(Field(968), 1, 0), 0, 32769));
    }

    [Fact]
    public void RepliesParseGraphicDataAndNativeLength()
    {
        var reply = Reply((0x380F, "04B00000000200410042"), (0x3810, "00040000000A"));
        var chunk = SqlProtocol.ParseLobChunk(reply, new(Field(968), 1, 0), 0, 2);
        Assert.Equal(new byte[] { 0, 65, 0, 66 }, chunk.Data.ToArray());
        Assert.Equal(10, chunk.TotalLength);
    }

    [Theory]
    [InlineData("0000", 0)]
    [InlineData("00040000000A", 10)]
    [InlineData("00080000000100000000", 4294967296L)]
    public void ZeroAmountQueriesReadEverySupportedLengthWidth(string total, long expected)
    {
        var chunk = SqlProtocol.ParseLobChunk(Reply((0x3810, total)), new(Field(960), 1, 0), 0, 0);
        Assert.Empty(chunk.Data.ToArray());
        Assert.Equal(expected, chunk.TotalLength);
    }

    [Theory]
    [InlineData("0001")]
    [InlineData("000400")]
    [InlineData("0004FFFFFFFF")]
    [InlineData("000000")]
    public void RejectsMalformedCurrentLength(string total)
        => Assert.Throws<Db2iException>(() =>
            SqlProtocol.ParseLobChunk(Reply((0x3810, total)), new(Field(960), 1, 0), 0, 0));

    [Theory]
    [InlineData("FFFF0000000201")]
    [InlineData("FFFF00000003010203")]
    [InlineData("FFFF00000000")]
    [InlineData("FFFF")]
    public void RejectsTruncatedOversizedOrNonProgressingData(string data)
        => Assert.Throws<Db2iException>(() => SqlProtocol.ParseLobChunk(
            Reply((0x3810, "000400000002"), (0x380F, data)), new(Field(960), 1, 0), 0, 2));

    // Descriptor offsets documented by JTOpen DBExtendedDataFormat and DBSuperExtendedDataFormat.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsesLocatorMetadataAndFourByteRowFootprint(bool super)
    {
        var payload = new byte[16 + (super ? 48 : 64)];
        BinaryPrimitives.WriteInt32BigEndian(payload, 1);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12), 4);
        var field = payload.AsSpan(16);
        BinaryPrimitives.WriteUInt16BigEndian(field, (ushort)(super ? 48 : 64));
        BinaryPrimitives.WriteUInt16BigEndian(field[2..], 965);
        BinaryPrimitives.WriteInt32BigEndian(field[4..], 4);
        BinaryPrimitives.WriteUInt16BigEndian(field[12..], 1208);
        BinaryPrimitives.WriteInt32BigEndian(field[17..], 0x01020304);
        BinaryPrimitives.WriteInt32BigEndian(field[26..], 100000000);
        var format = SqlProtocol.ParseResultFormat(Reply((super ? (ushort)0x3812 : (ushort)0x380C, Convert.ToHexString(payload))), 37)!;
        var actual = Assert.Single(format.Fields);
        Assert.Equal(4, actual.Length);
        Assert.Equal(0x01020304, actual.LobLocator);
        Assert.Equal(100000000, actual.LobMaxSize);
        Assert.True(actual.IsLobLocator);
        Assert.True(actual.ToColumn().IsLong);
        Assert.Equal("CLOB", actual.ToColumn().DataTypeName);
        Assert.Equal(100000000, actual.ToColumn().ColumnSize);
    }

    [Theory]
    [InlineData(404, "00000003000102", typeof(byte[]))]
    [InlineData(408, "00000003412020", typeof(string))]
    [InlineData(412, "00000003004100200020", typeof(string))]
    public void DecodesInlineLobsWithoutRemovingTrailingSpaces(int native, string content, Type expectedType)
    {
        var data = Convert.FromHexString(content);
        var field = new Db2iFieldDescriptor(native, data.Length, 0, 0, native == 412 ? 1200 : 1208, "LOB", 0);
        var format = new Db2iDataFormat(1, data.Length, 5, 2, [field]);
        var payload = Row(data, 1);
        var value = SqlProtocol.ParseResultBlock(Reply((0x380E, Convert.ToHexString(payload))), format).Rows[0][0];
        Assert.IsType(expectedType, value);
        if (value is string text) Assert.Equal("A  ", text);
        else Assert.Equal(new byte[] { 0, 1, 2 }, value);
    }

    [Fact]
    public void DecodesResultLocatorWithoutMaterialization()
    {
        var format = new Db2iDataFormat(1, 4, 5, 2, [Field(960)]);
        var block = Db2iTypeCodec.DecodeResultBlock(Row([1, 2, 3, 4], 1), format);
        var lob = Assert.IsType<Db2iLobValue>(Assert.Single(block.Rows)[0]);
        Assert.Equal(0x01020304, lob.Handle);
        Assert.Equal(0, lob.ColumnIndex);
    }

    [Theory]
    [InlineData(false, 960, 8, 100)]
    [InlineData(true, 964, 0, 100)]
    [InlineData(false, 404, 3, 100)]
    [InlineData(true, 412, 5, 100)]
    [InlineData(false, 968, 4, -1)]
    [InlineData(true, 452, -4, 0)]
    public void RejectsInvalidDescriptorStorage(bool super, int nativeType, int length, int maximum)
    {
        var payload = new byte[16 + (super ? 48 : 64)];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12), Math.Max(0, length));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(18), (ushort)nativeType);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(20), length);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(42), maximum);
        Assert.Throws<Db2iException>(() => SqlProtocol.ParseResultFormat(
            Reply((super ? (ushort)0x3812 : (ushort)0x380C, Convert.ToHexString(payload))), 37));
    }

    [Fact]
    public void RejectsResultDimensionOverflowAsProtocolError()
    {
        var payload = Row([], 1);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), int.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(16), 4);
        Assert.Throws<Db2iException>(() => Db2iTypeCodec.DecodeResultBlock(
            payload, new(1, 4, 5, 2, [Field(960)])));
    }

    [Fact]
    public void StreamDescriptorResolutionDoesNotReadAndEncodesOnlyHandle()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        stream.Position = 1;
        var parameter = new Db2iParameter { Value = stream };
        var field = Db2iTypeCodec.ResolveParameter(parameter, Field(960), 37, 0);
        var format = new Db2iDataFormat(1, 4, 5, 2, [field]);
        Assert.Equal(1, stream.Position);
        Assert.Equal(Convert.FromHexString("01020304"), Db2iTypeCodec.EncodeParameterRow(format, [parameter])[22..]);
        Assert.Equal(1, stream.Position);
    }

    [Fact]
    public void TypedNullPreservesServerLobTypeAndNullIndicator()
    {
        var parameter = new Db2iParameter { Value = DBNull.Value, DbType = DbType.String };
        var field = Db2iTypeCodec.ResolveParameter(parameter, Field(964), 37, 0);
        Assert.Equal(964, field.NativeType);
        var encoded = Db2iTypeCodec.EncodeParameterRow(new(1, 4, 5, 2, [field]), [parameter]);
        Assert.Equal(new byte[] { 255, 255 }, encoded[20..22]);
    }

    [Fact]
    public void StreamingValueWithNonLobMarkerRequiresExplicitCast()
    {
        using var text = new StringReader("text");
        var parameter = new Db2iParameter { Value = text };
        var error = Assert.Throws<NotSupportedException>(() =>
            Db2iTypeCodec.ResolveParameter(parameter, new(448, 20, 0, 0, 37, "", 0), 37, 0));
        Assert.Contains("CAST", error.Message);
    }

    [Fact]
    public void Utf8CcsidRoundTripsMultibyteAndSurrogates()
    {
        const string text = "è漢😀  ";
        Assert.Equal(text, IbmIEncoding.DecodeExact(1208, IbmIEncoding.Encode(1208, text)));
    }

    private static Db2iFieldDescriptor Field(int type)
        => new(type | 1, 4, 0, 0, type == 968 ? 1200 : type == 964 ? 1208 : 65535,
            "LOB", 0, 0x01020304, 100000000);

    private static byte[] Payload(byte[] request, ushort codePoint)
        => ClientAccessCodePoints.Find(request, 40, codePoint)!.Value.ToArray();

    private static DatabaseReply Reply(params (ushort CodePoint, string Hex)[] parameters)
        => new(SqlProtocol.RetrieveLobData, 0, 0,
            parameters.Select(value => new ClientAccessCodePoint(value.CodePoint, Convert.FromHexString(value.Hex))).ToArray(), null);

    private static byte[] Row(byte[] bytes, int columns)
    {
        var result = new byte[20 + bytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(result, 1);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8), (ushort)columns);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(16), bytes.Length);
        bytes.CopyTo(result, 20);
        return result;
    }
}
