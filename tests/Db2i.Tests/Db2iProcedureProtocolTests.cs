using System.Buffers.Binary;
using System.Data;
using Db2i.Protocol;

namespace Db2i.Tests;

public sealed class Db2iProcedureProtocolTests
{
    // Independent vectors based on IBM/JTOpen revision
    // 613335cea07e42679ddabaddcd51ce2cf5faaf24, DBExtendedDataFormat,
    // DBSuperExtendedDataFormat, DBReplySQLCA, DBSQLRequestDS, and JDCursor.
    // See docs/stored-procedure-protocol.md for links and offset definitions.
    private const string IntegerDescriptor =
        "01020304000000010000000000000004" +
        "004001F1000000040000000A0000F2" +
        "00000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000";

    private const string MixedOutputRow =
        "0102030400000001000300020000000000000016" +
        "000000000000" +
        "FFFFFFFFFFFFFFFFFFFF" + // Input-only DATE placeholder: never decode it.
        "0000002A" +
        "0004C1C240400000";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsesAndPreservesParameterModes(bool super)
    {
        foreach (var mode in new byte[] { 0, 0xF0, 0xF1, 0xF2, 0x7F })
        {
            var payload = Descriptor(super, mode);
            var format = SqlProtocol.ParseParameterFormat(
                Reply(((ushort)(super ? 0x3813 : 0x380D), payload)), 37)!;
            var field = Assert.Single(format.Fields);

            Assert.Equal((Db2iParameterMode)mode, field.ParameterMode);
            Assert.Equal(mode is 0xF0 or 0xF2, field.IsInputParameter);
            Assert.Equal(mode is 0xF1 or 0xF2, field.IsOutputParameter);
            Assert.Equal(496, field.NativeType);
            Assert.Equal(4, field.Length);
            Assert.Equal(10, field.Precision);
            Assert.False(field.IsArray);

            var request = SqlProtocol.CreateChangeDescriptorRequest(3, format);
            Assert.Equal(Descriptor(false, mode), Payload(request, 0x381E));
        }
    }

    [Fact]
    public void InputResolutionRetainsServerMode()
    {
        var serverField = IntegerField(Db2iParameterMode.Input);
        var field = Db2iTypeCodec.ResolveParameter(new Db2iParameter("p", 9), serverField, 37, 0);
        Assert.Equal(Db2iParameterMode.Input, field.ParameterMode);
        Assert.Equal(0xF0, Payload(SqlProtocol.CreateChangeDescriptorRequest(3, Format(field)), 0x381E)[30]);
    }

    [Fact]
    public void PreservesArrayFlagAndRejectsScalarBinding()
    {
        var payload = Descriptor(true, 0xF2);
        payload[37] = 0x40;
        var format = SqlProtocol.ParseParameterFormat(Reply((0x3813, payload)), 37)!;
        var field = Assert.Single(format.Fields);
        Assert.True(field.IsArray);
        Assert.Throws<NotSupportedException>(() => SqlProtocol.CreateChangeDescriptorRequest(3, format));
        Assert.Throws<NotSupportedException>(() => Db2iTypeCodec.ResolveParameter(new Db2iParameter("p", 9), field, 37, 0));
        Assert.Throws<NotSupportedException>(() => SqlProtocol.ParseProcedureCompletion(CompletionReply(), format));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedParameterDescriptorsFailBeforeReadingMode(bool super)
    {
        var payload = Descriptor(super, 0xF1);
        for (var length = 0; length < payload.Length; length++)
        {
            // A completely empty parameter format is the established no-marker representation.
            if (length == 0) continue;
            AssertProtocolError(() => SqlProtocol.ParseParameterFormat(
                Reply(((ushort)(super ? 0x3813 : 0x380D), payload[..length])), 37));
        }
    }

    [Fact]
    public void PrepareRequestsCallTypeAndBothDescriptorsWithoutFetchingData()
    {
        var request = SqlProtocol.CreatePrepareRequest(3, "CALL P(?)", Db2iStatementKind.Procedure);
        Assert.Equal(SqlProtocol.PrepareDescribe, ClientAccessHeader.Parse(request).RequestReplyId);
        Assert.Equal(Convert.FromHexString("FA800000"), request[20..24]);
        Assert.Equal(Convert.FromHexString("0003"), Payload(request, 0x3812));
        Assert.Equal(Convert.FromHexString("00"), Payload(request, 0x3808));
    }

    [Fact]
    public void ParameterlessExecuteMatchesWireVector()
    {
        var request = SqlProtocol.CreateProcedureExecuteRequest(3, null, returnOutputParameters: false);
        Assert.Equal(Convert.FromHexString(
            "000000300000E004000000000000000300141805" +
            "F200000000000000000300030000000300000001" +
            "0000000838120003"), request);
    }

    [Theory]
    [InlineData(false, "F2000000")]
    [InlineData(true, "F6000000")]
    public void ExecuteRequestsOutputDataOnlyWhenNeeded(bool outputs, string bitmap)
    {
        var data = Convert.FromHexString("0000000100000001000100020000000000000004000000000007");
        var request = SqlProtocol.CreateProcedureExecuteRequest(3, data, outputs);
        Assert.Equal(Convert.FromHexString(
            "000000500000E004000000000000000300141805" +
            bitmap + "00000000000300030000000300030002" +
            "0000000838120003" +
            "00000020381F0000000100000001000100020000000000000004000000000007"), request);
    }

    [Fact]
    public void RejectsInvalidProcedureRequestPayloads()
    {
        Assert.Throws<ArgumentException>(() => SqlProtocol.CreateProcedureExecuteRequest(3, null, true));
        Assert.Throws<ArgumentException>(() => SqlProtocol.CreateProcedureExecuteRequest(3, Array.Empty<byte>(), true));
        Assert.Throws<ArgumentException>(() => SqlProtocol.CreateProcedureExecuteRequest(3, new byte[20], true));
        var row = Convert.FromHexString("0000000100000002000100020000000000000004000000000007");
        Assert.Throws<ArgumentException>(() => SqlProtocol.CreateProcedureExecuteRequest(3, row, true));
    }

    [Fact]
    public void OpenResultDescribesWithoutPrefetchOrParameterBinding()
    {
        var request = SqlProtocol.CreateProcedureOpenResultRequest(3);
        Assert.Equal(Convert.FromHexString(
            "000000370000E004000000000000000300141804" +
            "FA00000000000000000300030000000300000002" +
            "0000000738098000000008380D0005"), request);
    }

    [Fact]
    public void CloseResultAdvancesChainWithoutRequestingReply()
    {
        var request = SqlProtocol.CreateProcedureCloseResultRequest(3);
        Assert.Equal(Convert.FromHexString(
            "0000002F0000E00400000000000000030014180A" +
            "0004000000000000000300030000000300000001" +
            "000000073810F2"), request);
        Assert.Equal(new byte[] { 0xF1 }, Payload(SqlProtocol.CreateCloseRequest(3), 0x3810));
    }

    [Fact]
    public void CompletionSeparatesStatusResultsAndOutputOrdinals()
    {
        var completion = SqlProtocol.ParseProcedureCompletion(
            CompletionReply(Convert.FromHexString(MixedOutputRow), returnStatus: -7, results: 2), MixedFormat());

        Assert.Equal(-7, completion.ReturnStatus);
        Assert.Equal(2, completion.ResultSetCount);
        Assert.Collection(completion.Outputs,
            output => { Assert.Equal(1, output.ParameterIndex); Assert.Equal(42, output.Value); },
            output => { Assert.Equal(2, output.ParameterIndex); Assert.Equal("AB  ", output.Value); });
    }

    [Fact]
    public void OutputNullAndEmptyStringAreDistinct()
    {
        var row = Convert.FromHexString(MixedOutputRow);
        row[22] = row[23] = 0xFF; // Null OUT integer; ignore its existing value bytes.
        row[40] = row[41] = 0; // Empty INOUT VARCHAR.
        var completion = SqlProtocol.ParseProcedureCompletion(CompletionReply(row), MixedFormat());
        Assert.Null(completion.Outputs[0].Value);
        Assert.Equal(string.Empty, completion.Outputs[1].Value);
    }

    [Fact]
    public void DynamicResultDescriptorDoesNotReplaceParameterDescriptor()
    {
        var reply = CompletionReply(Convert.FromHexString(MixedOutputRow), results: 1);
        reply = reply with { CodePoints = [.. reply.CodePoints, new(0x380C, Descriptor(false, 0))] };
        var completion = SqlProtocol.ParseProcedureCompletion(reply, MixedFormat());
        Assert.Equal(2, completion.Outputs.Count);
        Assert.Equal("AB  ", completion.Outputs[1].Value);
    }

    [Fact]
    public void SupportsRowsWithoutNullIndicatorsAndWithRecordPadding()
    {
        var original = Convert.FromHexString(MixedOutputRow);
        byte[] row = [.. original[..20], .. original[26..], 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(10), 0);
        BinaryPrimitives.WriteInt32BigEndian(row.AsSpan(16), 24);
        var completion = SqlProtocol.ParseProcedureCompletion(CompletionReply(row), MixedFormat());
        Assert.Equal(42, completion.Outputs[0].Value);
        Assert.Equal("AB  ", completion.Outputs[1].Value);
    }

    [Fact]
    public void ZeroWidthRowsCannotBypassProcedureRowCountLimit()
    {
        var row = Convert.FromHexString("000000017FFFFFFF000000000000000000000000");
        AssertProtocolError(() => Db2iTypeCodec.DecodeProcedureOutputRow(row, new(1, 0, 5, 2, [])));
    }

    [Fact]
    public void OutputLocatorIsNotMaterializedOrConfusedWithDescriptorHandle()
    {
        var field = new Db2iFieldDescriptor(961, 4, 0, 0, 65535, "LOB", 0,
            LobLocator: 99, LobMaxSize: 100000000, ParameterMode: Db2iParameterMode.Output);
        var row = Convert.FromHexString("0000000100000001000100020000000000000004000001020304");
        var output = Assert.Single(SqlProtocol.ParseProcedureCompletion(CompletionReply(row), Format(field)).Outputs);
        var locator = Assert.IsType<Db2iLobValue>(output.Value);
        Assert.Equal(0x01020304, locator.Handle);
        Assert.Equal(0, locator.ColumnIndex);
        Assert.Same(field, locator.Field);
    }

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, 32767)]
    public void StatusAndResultCountDoNotRequireParameters(int status, int results)
    {
        var completion = SqlProtocol.ParseProcedureCompletion(CompletionReply(returnStatus: status, results: results), null);
        Assert.Equal(status, completion.ReturnStatus);
        Assert.Equal(results, completion.ResultSetCount);
        Assert.Empty(completion.Outputs);
    }

    [Fact]
    public void InputOnlyProcedureDoesNotRequireOutputData()
    {
        var completion = SqlProtocol.ParseProcedureCompletion(CompletionReply(), Format(IntegerField(Db2iParameterMode.Input)));
        Assert.Empty(completion.Outputs);
    }

    [Fact]
    public void MissingOrTruncatedSqlcaIsNotAZeroStatus()
    {
        AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(Reply(), null));
        for (var length = 0; length < 136; length++)
        {
            AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(Reply((0x3807, new byte[length])), null));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32768)]
    [InlineData(int.MaxValue)]
    public void RejectsUnboundedResultCounts(int results)
        => AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(CompletionReply(results: results), null));

    [Fact]
    public void RejectsWrongReplyAndMissingOrUnexpectedOutputData()
    {
        AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(CompletionReply() with { ReturnFunctionId = SqlProtocol.Fetch }, null));
        AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(CompletionReply(), MixedFormat()));
        AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(CompletionReply([]), MixedFormat()));
        AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(CompletionReply(Convert.FromHexString(MixedOutputRow)), null));
        AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(CompletionReply(), Format(IntegerField(Db2iParameterMode.Unspecified))));
        AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(CompletionReply(), Format(IntegerField((Db2iParameterMode)0xFF))));

        var reply = CompletionReply(Convert.FromHexString(MixedOutputRow));
        reply = reply with { CodePoints = [.. reply.CodePoints, new(0x380E, Convert.FromHexString(MixedOutputRow))] };
        AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(reply, MixedFormat()));
    }

    [Fact]
    public void RejectsEveryTruncationOfAnOutputRow()
    {
        var row = Convert.FromHexString(MixedOutputRow);
        for (var length = 0; length < row.Length; length++)
        {
            AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(CompletionReply(row[..length]), MixedFormat()));
        }
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 2)]
    [InlineData(4, int.MaxValue)]
    [InlineData(4, -1)]
    [InlineData(16, 21)]
    [InlineData(16, int.MaxValue)]
    [InlineData(16, -1)]
    public void RejectsInvalidOutputDimensionsBeforeAllocation(int offset, int value)
    {
        var row = Convert.FromHexString(MixedOutputRow);
        BinaryPrimitives.WriteInt32BigEndian(row.AsSpan(offset), value);
        AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(CompletionReply(row), MixedFormat()));
    }

    [Theory]
    [InlineData(8, 2)]
    [InlineData(8, 4)]
    [InlineData(10, 1)]
    [InlineData(10, 4)]
    public void RejectsMismatchedColumnsAndIndicators(int offset, ushort value)
    {
        var row = Convert.FromHexString(MixedOutputRow);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(offset), value);
        AssertProtocolError(() => SqlProtocol.ParseProcedureCompletion(CompletionReply(row), MixedFormat()));
    }

    [Fact]
    public async Task ExistingInputExecutionRoundTripsDirectionThroughTheHostDescriptor()
    {
        var scenario = new FakeQueryScenario(null, Format(IntegerField(Db2iParameterMode.Input)), [], RowsAffected: 1);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = new Db2iConnection(
            $"Server=127.0.0.1;Port={server.Port};User ID=TESTUSER;Password=secret;Pooling=false");
        await connection.OpenAsync();
        await using var command = new Db2iCommand("UPDATE TEST_TABLE SET VALUE = ?", connection);
        command.Parameters.Add("p", 9);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        var field = Assert.Single(server.LastClientParameterFormat!.Fields);
        Assert.Equal(Db2iParameterMode.Input, field.ParameterMode);
        Assert.Equal(new byte[] { 0, 0, 0, 9 }, server.LastParameterData!.Value.Span[22..].ToArray());
    }

    [Theory]
    [InlineData(CommandType.Text, "CALL P(?)")]
    [InlineData(CommandType.StoredProcedure, "P")]
    public async Task ProtocolGroundworkDoesNotEnableIncompletePublicExecution(CommandType type, string text)
    {
        await using var server = new FakeIbmIHostServer();
        await using var connection = new Db2iConnection(
            $"Server=127.0.0.1;Port={server.Port};User ID=TESTUSER;Password=secret;Pooling=false");
        await connection.OpenAsync();
        await using var command = new Db2iCommand(text, connection) { CommandType = type };
        await Assert.ThrowsAsync<NotSupportedException>(() => command.PrepareAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync());
        Assert.Empty(server.PreparedStatements);
        Assert.Empty(server.ExecutedSql);
    }

    private static byte[] Descriptor(bool super, byte mode)
    {
        var bytes = Convert.FromHexString(IntegerDescriptor);
        Assert.Equal(80, bytes.Length);
        if (super) bytes = bytes[..64];
        bytes[17] = super ? (byte)48 : (byte)64;
        bytes[30] = mode;
        return bytes;
    }

    private static Db2iFieldDescriptor IntegerField(Db2iParameterMode mode)
        => new(497, 4, 0, 10, 0, "", 0, ParameterMode: mode);

    private static Db2iDataFormat Format(Db2iFieldDescriptor field)
        => new(1, field.Length, 5, 2, [field]);

    private static Db2iDataFormat MixedFormat()
        => new(0x01020304, 22, 5, 2,
        [
            new(385, 10, 0, 10, 37, "IN_DATE", 0, ParameterMode: Db2iParameterMode.Input),
            IntegerField(Db2iParameterMode.Output) with { Offset = 10 },
            new(449, 8, 0, 6, 37, "INOUT_TEXT", 14, ParameterMode: Db2iParameterMode.InputOutput),
        ]);

    private static DatabaseReply CompletionReply(byte[]? row = null, int returnStatus = 0, int results = 0)
    {
        var sqlca = new byte[136];
        BinaryPrimitives.WriteInt32BigEndian(sqlca.AsSpan(96), returnStatus);
        BinaryPrimitives.WriteInt32BigEndian(sqlca.AsSpan(100), results);
        BinaryPrimitives.WriteInt32BigEndian(sqlca.AsSpan(104), 12345); // Deliberately different from status/results.
        return row is null ? Reply((0x3807, sqlca)) : Reply((0x3807, sqlca), (0x380E, row));
    }

    private static DatabaseReply Reply(params (ushort CodePoint, byte[] Payload)[] parameters)
        => new(SqlProtocol.Execute, 0, 0,
            parameters.Select(value => new ClientAccessCodePoint(value.CodePoint, value.Payload)).ToArray(), null);

    private static byte[] Payload(byte[] request, ushort codePoint)
        => ClientAccessCodePoints.Find(request, 40, codePoint)!.Value.ToArray();

    private static void AssertProtocolError(Action action)
        => Assert.Equal(Db2iErrorKind.Protocol, Assert.Throws<Db2iException>(action).Kind);
}
