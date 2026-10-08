using System.Data;
using System.Data.Common;
using System.Text;
using Db2i.Protocol;

namespace Db2i.Tests;

public sealed class Db2iLobTests
{
    [Fact]
    public async Task LobMetadataNullEmptyAndMultipleColumnsRetainTheirTypes()
    {
        var scenario = Query(
            [Field(960, "B", 0), Field(964, "C", 4, 1208), Field(968, "D", 8, 1200)],
            [[[11, 12, 13], [null, null, null], [14, 15, 16]]],
            new Dictionary<int, FakeLobData>
            {
                [11] = new([1, 2, 3]),
                [12] = new(Encoding.UTF8.GetBytes("città  "), 1208),
                [13] = new(Encoding.BigEndianUnicode.GetBytes("日本語  "), 1200, true),
                [14] = new([]), [15] = new([], 1208), [16] = new([], 1200, true),
            });
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("SELECT B,C,D FROM LOBS", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.Equal(0, server.LobThreshold);
        Assert.Equal((byte)0xD5, server.CloseOnEof);
        Assert.Equal("BLOB", reader.GetDataTypeName(0));
        Assert.Equal("CLOB", reader.GetDataTypeName(1));
        Assert.Equal("DBCLOB", reader.GetDataTypeName(2));
        Assert.Equal(typeof(byte[]), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetFieldType(1));
        var schema = reader.GetColumnSchema();
        Assert.All(schema, column => Assert.True(column.IsLong));
        Assert.All(schema, column => Assert.Equal(100_000_000, column.ColumnSize));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.GetValue(0));
        Assert.Equal("città  ", reader.GetString(1));
        Assert.Equal("日本語  ", await reader.GetFieldValueAsync<string>(2));
        Assert.True(await reader.ReadAsync());
        Assert.All(Enumerable.Range(0, 3), ordinal => Assert.True(reader.IsDBNull(ordinal)));
        Assert.True(await reader.ReadAsync());
        Assert.Empty(await reader.GetFieldValueAsync<byte[]>(0));
        Assert.Equal(string.Empty, reader.GetString(1));
        Assert.Equal(string.Empty, reader.GetTextReader(2).ReadToEnd());
        Assert.False(await reader.ReadAsync());
        await reader.CloseAsync();
        Assert.Equal(new[] { 11, 12, 13, 14, 15, 16 }, server.FreedLobHandles.Order());
    }

    [Fact]
    public async Task PartialBinaryReadsAndNonseekableStreamCrossChunkBoundaries()
    {
        var bytes = Enumerable.Range(0, 150_003).Select(i => (byte)(i % 251)).ToArray();
        await using var server = new FakeIbmIHostServer(queryScenario: BinaryQuery(bytes));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES BLOB_VALUE", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(bytes.Length, reader.GetBytes(0, 0, null, 0, 0));
        var slice = new byte[10];
        Assert.Equal(7, reader.GetBytes(0, 65_533, slice, 2, 7));
        Assert.Equal(bytes.AsSpan(65_533, 7).ToArray(), slice.AsSpan(2, 7).ToArray());
        await using var stream = reader.GetStream(0);
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(bytes, output.ToArray());
        Assert.All(server.LobReads, request => Assert.InRange(request.Amount, 0, 65_536));
        Assert.Contains(server.LobReads, request => request.Offset >= 65_536);
    }

    [Theory]
    [InlineData(964, 1208)]
    [InlineData(968, 1200)]
    public async Task TextReadersPreserveMultibyteSurrogatesAndTrailingSpaces(int type, int ccsid)
    {
        var text = new string('a', 65_535) + "😀è日本語" + new string('z', 40_000) + "   ";
        var encoding = ccsid == 1208 ? Encoding.UTF8 : Encoding.BigEndianUnicode;
        var scenario = Query([Field(type, "TEXT", 0, ccsid)], [[[11]]],
            new Dictionary<int, FakeLobData> { [11] = new(encoding.GetBytes(text), ccsid, type == 968) });
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES TEXT_VALUE", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(text.Length, reader.GetChars(0, 0, null, 0, 0));
        var part = new char[12];
        Assert.Equal(12, reader.GetChars(0, 65_533, part, 0, part.Length));
        Assert.Equal(text.Substring(65_533, part.Length), new string(part));
        using var stream = reader.GetTextReader(0);
        Assert.Equal(text, await stream.ReadToEndAsync());
        Assert.Equal(text, await reader.GetFieldValueAsync<string>(0));
        Assert.All(server.LobReads, request => Assert.InRange(request.Amount * (type == 968 ? 2 : 1), 0, 65_536));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextReaderPeekReadAndReadLinePreserveContentAndInvalidateBufferedCharacters(bool batched)
    {
        var scenario = Query([Field(964, "C", 0, 1208)], [[[11], [12]]],
            new Dictionary<int, FakeLobData>
            {
                [11] = new(Encoding.UTF8.GetBytes("a\r\n😀line\nend  "), 1208),
                [12] = new(Encoding.UTF8.GetBytes("next"), 1208),
            });
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES CLOB_VALUE", connection);
        await using var batch = new Db2iBatch(connection);
        batch.BatchCommands.Add(new Db2iBatchCommand("VALUES CLOB_VALUE"));
        await using var reader = batched ? await batch.ExecuteReaderAsync() : await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        using (var text = reader.GetTextReader(0))
        {
            Assert.Equal('a', text.Peek());
            Assert.Equal('a', text.Peek());
            Assert.Equal('a', text.Read());
            Assert.Equal(string.Empty, text.ReadLine());
            Assert.Equal("😀line", await text.ReadLineAsync());
            Assert.Equal("end  ", await text.ReadToEndAsync());
            Assert.Equal(-1, text.Peek());
        }

        using var stale = reader.GetTextReader(0);
        Assert.Equal('a', stale.Peek());
        Assert.True(await reader.ReadAsync());
        Assert.ThrowsAny<InvalidOperationException>(() => stale.Peek());
        Assert.ThrowsAny<InvalidOperationException>(() => stale.Read());
        Assert.Equal("next", reader.GetString(0));
    }

    [Fact]
    public async Task DownloadLargerThanPacketLimitUsesBoundedRequests()
    {
        var bytes = new byte[(16 * 1024 * 1024) + 97];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        await using var server = new FakeIbmIHostServer(queryScenario: BinaryQuery(bytes));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES BIG_BLOB", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        await using var stream = reader.GetStream(0);
        var buffer = new byte[17_003];
        var offset = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer)) != 0)
        {
            Assert.True(bytes.AsSpan(offset, count).SequenceEqual(buffer.AsSpan(0, count)));
            offset += count;
        }
        Assert.Equal(bytes.Length, offset);
        Assert.All(server.LobReads, request => Assert.InRange(request.Amount, 0, 65_536));
    }

    [Fact]
    public async Task MaterializationLimitRejectsLargeBlobButStreamingRemainsAvailable()
    {
        var scenario = BinaryQuery([1, 2, 3]) with
        {
            Lobs = new Dictionary<int, FakeLobData> { [11] = new([1, 2, 3], LogicalLength: 64L * 1024 * 1024 + 1) },
        };
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES HUGE_BLOB", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
        await using var stream = reader.GetStream(0);
        Assert.Equal(3, await stream.ReadAsync(new byte[3]));
        Assert.DoesNotContain(server.LobReads, request => request.Amount > 65_536);
    }

    [Fact]
    public async Task TextMaterializationLimitCountsManagedCharactersRatherThanEncodedBytes()
    {
        var bytes = new byte[(32 * 1024 * 1024) + 1];
        Array.Fill(bytes, (byte)'a');
        var scenario = Query([Field(964, "C", 0, 1208)], [[[11]]],
            new Dictionary<int, FakeLobData> { [11] = new(bytes, 1208) });
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES LARGE_CLOB", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetFieldValueAsync<string>(0));
        Assert.Contains("GetTextReader", error.Message);
        using var text = reader.GetTextReader(0);
        var sample = new char[7];
        Assert.Equal(sample.Length, await text.ReadAsync(sample));
        Assert.All(sample, value => Assert.Equal('a', value));
    }

    [Fact]
    public async Task SequentialAccessRejectsBackwardOffsetsAndOrdinals()
    {
        var scenario = Query([Field(960, "B1", 0), Field(960, "B2", 4)], [[[11, 12]]],
            new Dictionary<int, FakeLobData> { [11] = new([1, 2, 3]), [12] = new([4, 5, 6]) });
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("SELECT B1,B2 FROM LOBS", connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetBytes(0, 1, new byte[1], 0, 1));
        Assert.Throws<InvalidOperationException>(() => reader.GetBytes(0, 0, new byte[1], 0, 1));
        Assert.Equal(1, reader.GetBytes(1, 0, new byte[1], 0, 1));
        Assert.Throws<InvalidOperationException>(() => reader.GetBytes(0, 2, new byte[1], 0, 1));
    }

    [Fact]
    public async Task SequentialAccessRejectsBackwardNullChecks()
    {
        var scenario = Query([Field(960, "B1", 0), Field(960, "B2", 4)], [[[null, 12]]],
            new Dictionary<int, FakeLobData> { [12] = new([4, 5, 6]) });
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("SELECT B1,B2 FROM LOBS", connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.False(await reader.IsDBNullAsync(1));
        Assert.Throws<InvalidOperationException>(() => reader.IsDBNull(0));
    }

    [Fact]
    public async Task AdvancingAndClosingInvalidateStreamsAndFreeUnreadRows()
    {
        var scenario = Query([Field(960, "B", 0)], [[[11], [12], [13]], [[14]]],
            Enumerable.Range(11, 4).ToDictionary(handle => handle, _ => new FakeLobData([1, 2, 3])));
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("SELECT B FROM LOBS", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        using var first = reader.GetStream(0);
        using var second = reader.GetStream(0);
        Assert.ThrowsAny<InvalidOperationException>(() => first.ReadByte());
        Assert.True(await reader.ReadAsync());
        Assert.ThrowsAny<InvalidOperationException>(() => second.ReadByte());
        using var third = reader.GetStream(0);
        await reader.CloseAsync();
        Assert.ThrowsAny<InvalidOperationException>(() => third.ReadByte());
        Assert.Equal(new[] { 11, 12, 13 }, server.FreedLobHandles.Order());
        Assert.DoesNotContain(14, server.FreedLobHandles);
    }

    [Fact]
    public async Task ReadingMultipleRowBlocksReleasesOnlyTheCompletedBlock()
    {
        var scenario = Query([Field(960, "B", 0)], [[[11], [12]], [[13]]],
            new Dictionary<int, FakeLobData> { [11] = new([1]), [12] = new([2]), [13] = new([3]) });
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("SELECT B FROM LOBS", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(new byte[] { 1 }, reader.GetValue(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(new byte[] { 2 }, reader.GetValue(0));
        Assert.Empty(server.FreedLobHandles);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(new byte[] { 3 }, reader.GetValue(0));
        Assert.Equal(new[] { 11, 12 }, server.FreedLobHandles.Order());
        Assert.False(await reader.ReadAsync());
        Assert.Equal(new[] { 11, 12, 13 }, server.FreedLobHandles.Order());
        Assert.Equal(1, server.FetchCount);
    }

    [Fact]
    public async Task BlobInputsUseCurrentPositionPrepareDoesNotConsumeAndReuseTruncates()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: InputScenario(960));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("INSERT INTO LOBS(B) VALUES (?)", connection);
        using var source = new NonSeekableInput(new byte[130_017]);
        Assert.Equal(9, source.Read(new byte[9]));
        var parameter = command.Parameters.Add("b", source);
        await command.PrepareAsync();
        Assert.Equal(9, source.BytesRead);
        Assert.Empty(server.LobWrites);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.False(source.WasDisposed);
        Assert.Equal(130_008, server.ExecutedLobs[0][21].Length);
        parameter.Value = new byte[] { 8, 9 };
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        parameter.Value = Array.Empty<byte>();
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.Equal(new byte[] { 8, 9 }, server.ExecutedLobs[1][21]);
        Assert.Empty(server.ExecutedLobs[2][21]);
        Assert.Single(server.PreparedStatements);
        Assert.Equal(3, server.LobWrites.Count(request => request.Truncate && request.Offset == 0));
        Assert.All(server.LobWrites, request => Assert.InRange(request.Amount, 0, 65_536));
    }

    [Theory]
    [InlineData(964, 1208)]
    [InlineData(968, 1200)]
    public async Task TextReaderInputsPreserveSplitSurrogatesAndRemainOpen(int type, int ccsid)
    {
        var text = new string('x', 32_767) + "😀è日本語" + new string('y', 50_000) + "   ";
        await using var server = new FakeIbmIHostServer(queryScenario: InputScenario(type, ccsid));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("INSERT INTO LOBS(T) VALUES (?)", connection);
        using var source = new TrackingTextReader("skip" + text);
        Assert.Equal(4, source.Read(new char[4]));
        command.Parameters.Add("t", source);
        await command.PrepareAsync();
        Assert.Equal(4, source.CharsRead);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.False(source.WasDisposed);
        var encoding = ccsid == 1208 ? Encoding.UTF8 : Encoding.BigEndianUnicode;
        Assert.Equal(encoding.GetBytes(text), server.UploadedLobs[21]);
        Assert.All(server.LobWrites, request => Assert.InRange(request.Amount * (type == 968 ? 2 : 1), 0, 65_536));
    }

    [Fact]
    public async Task UploadLargerThanPacketLimitUsesBoundedWrites()
    {
        var bytes = new byte[(16 * 1024 * 1024) + 97];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        await using var server = new FakeIbmIHostServer(queryScenario: InputScenario(960));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("INSERT INTO LOBS(B) VALUES (?)", connection);
        using var source = new NonSeekableInput(bytes);
        command.Parameters.Add("b", source);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.Equal(bytes, server.UploadedLobs[21]);
        Assert.False(source.WasDisposed);
        Assert.All(server.LobWrites, request => Assert.InRange(request.Amount, 0, 65_536));
    }

    [Theory]
    [InlineData(964, 1208)]
    [InlineData(968, 1200)]
    public async Task ReusingTextParameterWithShorterEmptyAndNullValuesLeavesNoOldContent(int type, int ccsid)
    {
        await using var server = new FakeIbmIHostServer(queryScenario: InputScenario(type, ccsid));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("INSERT INTO LOBS(T) VALUES (?)", connection);
        var parameter = command.Parameters.Add("t", new StringReader(new string('x', 70_000)));
        await command.ExecuteNonQueryAsync();
        parameter.Value = new StringReader("è😀  ");
        await command.ExecuteNonQueryAsync();
        parameter.Value = new StringReader(string.Empty);
        await command.ExecuteNonQueryAsync();
        parameter.Value = DBNull.Value;
        await command.ExecuteNonQueryAsync();
        var encoding = ccsid == 1208 ? Encoding.UTF8 : Encoding.BigEndianUnicode;
        Assert.Equal(encoding.GetBytes("è😀  "), server.ExecutedLobs[1][21]);
        Assert.Empty(server.ExecutedLobs[2][21]);
        Assert.Equal(3, server.LobWrites.Count(request => request.Truncate && request.Offset == 0));
        Assert.Single(server.PreparedStatements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParameterSizeRejectsExcessWithoutExecutingAndCanBeRetried(bool text)
    {
        await using var server = new FakeIbmIHostServer(queryScenario: InputScenario(text ? 964 : 960, text ? 1208 : 65535));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("INSERT INTO LOBS(V) VALUES (?)", connection);
        var parameter = command.Parameters.Add("v", text ? new StringReader("èèè") : new MemoryStream([1, 2, 3]));
        parameter.Size = 2;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => command.ExecuteNonQueryAsync());
        Assert.Empty(server.ExecutedSql);
        parameter.Value = text ? "èè" : new byte[] { 1, 2 };
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.Equal(text ? Encoding.UTF8.GetBytes("èè") : new byte[] { 1, 2 }, server.UploadedLobs[21]);
    }

    [Fact]
    public async Task NullParameterDoesNotUploadData()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: InputScenario(960));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("INSERT INTO LOBS(B) VALUES (?)", connection);
        command.Parameters.Add("b", DBNull.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.Empty(server.LobWrites);
        var data = Assert.IsType<ReadOnlyMemory<byte>>(server.LastParameterData);
        Assert.Equal((byte)0xFF, data.Span[20]);
        Assert.Equal((byte)0xFF, data.Span[21]);
    }

    [Fact]
    public async Task RollbackInvalidatesPreparedInputLocators()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: InputScenario(960));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("INSERT INTO LOBS(B) VALUES (?)", connection);
        command.Parameters.Add("b", new byte[] { 1 });
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            command.Transaction = (Db2iTransaction)transaction;
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }
        command.Transaction = null;
        await command.ExecuteNonQueryAsync();
        Assert.Equal(2, server.PreparedStatements.Count);
        Assert.Equal(2, server.ExecutedLobs.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringDownloadOrUploadCancelsTheHostOperation(bool upload)
    {
        var scenario = upload ? InputScenario(960) with { StallOnLobWrite = true }
            : BinaryQuery([1, 2, 3]) with { StallOnLobRead = true };
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand(upload ? "INSERT INTO LOBS(B) VALUES (?)" : "VALUES BLOB_VALUE", connection);
        using var cancellation = new CancellationTokenSource();
        DbDataReader? reader = null;
        Task operation;
        if (upload)
        {
            command.Parameters.Add("b", new MemoryStream([1, 2, 3]));
            operation = command.ExecuteNonQueryAsync(cancellation.Token);
        }
        else
        {
            reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            operation = reader.GetStream(0).ReadAsync(new byte[1], cancellation.Token).AsTask();
        }
        await WaitForAsync(() => upload ? server.LobWrites.Count != 0 : server.LobReads.Count != 0);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.NotNull(server.CancelTargetJobIdentifier);
        if (reader is not null) await reader.DisposeAsync();
        if (upload)
        {
            command.Parameters[0].Value = new byte[] { 9 };
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            Assert.Equal(2, server.PreparedStatements.Count);
            Assert.Equal(new byte[] { 9 }, server.UploadedLobs[21]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadTimeoutInvalidatesPreparationAndRetainsBatchAttribution(bool batched)
    {
        await using var server = new FakeIbmIHostServer(queryScenario: InputScenario(960) with { StallOnLobWrite = true });
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        if (batched)
        {
            await using var batch = new Db2iBatch(connection) { Timeout = 1 };
            var insert = new Db2iBatchCommand("INSERT INTO LOBS(B) VALUES (?)");
            insert.Parameters.Add("b", new MemoryStream([1, 2, 3]));
            batch.BatchCommands.Add(insert);
            var error = await Assert.ThrowsAsync<Db2iException>(() => batch.ExecuteNonQueryAsync());
            Assert.Equal(Db2iErrorKind.Timeout, error.Kind);
            Assert.Same(insert, error.BatchCommand);
            insert.Parameters[0].Value = Array.Empty<byte>();
            Assert.Equal(1, await batch.ExecuteNonQueryAsync());
        }
        else
        {
            await using var command = new Db2iCommand("INSERT INTO LOBS(B) VALUES (?)", connection) { CommandTimeout = 1 };
            command.Parameters.Add("b", new MemoryStream([1, 2, 3]));
            var error = await Assert.ThrowsAsync<Db2iException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(Db2iErrorKind.Timeout, error.Kind);
            command.Parameters[0].Value = Array.Empty<byte>();
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        Assert.NotNull(server.CancelTargetJobIdentifier);
        Assert.Equal(2, server.PreparedStatements.Count);
        Assert.Single(server.ExecutedSql);
        Assert.Empty(server.UploadedLobs[21]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchLobTimeoutRetainsAttributionForGetterAndStream(bool stream)
    {
        await using var server = new FakeIbmIHostServer(queryScenario: BinaryQuery([1, 2, 3]) with { StallOnLobRead = true });
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = new Db2iBatch(connection) { Timeout = 1 };
        var query = new Db2iBatchCommand("VALUES BLOB_VALUE");
        batch.BatchCommands.Add(query);
        batch.BatchCommands.Add(new Db2iBatchCommand("VALUES NOT_REACHED"));
        await using var reader = await batch.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var error = stream
            ? await Assert.ThrowsAsync<Db2iException>(async () => await reader.GetStream(0).ReadAsync(new byte[1]))
            : await Assert.ThrowsAsync<Db2iException>(() => reader.GetFieldValueAsync<byte[]>(0));
        Assert.Equal(Db2iErrorKind.Timeout, error.Kind);
        Assert.Same(query, error.BatchCommand);
        Assert.Single(server.ExecutedSql);
    }

    [Fact]
    public async Task BatchLobServerErrorRetainsAttributionForSynchronousGetter()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: BinaryQuery([1]) with { LobReadSqlCode = -443 });
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = new Db2iBatch(connection);
        var query = new Db2iBatchCommand("VALUES BLOB_VALUE");
        batch.BatchCommands.Add(query);
        await using var reader = await batch.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var error = Assert.Throws<Db2iException>(() => reader.GetBytes(0, 0, new byte[1], 0, 1));
        Assert.Equal(-443, error.SqlCode);
        Assert.Same(query, error.BatchCommand);
    }

    [Fact]
    public async Task BatchNextResultInvalidatesStreamAndReleasesUnreadLobs()
    {
        var first = BinaryQuery([1, 2, 3]) with { Blocks = [[[11], [12]]], Lobs = new Dictionary<int, FakeLobData> { [11] = new([1]), [12] = new([2]) } };
        var second = BinaryQuery([3]) with { Blocks = [[[13]]], Lobs = new Dictionary<int, FakeLobData> { [13] = new([3]) } };
        await using var server = new FakeIbmIHostServer(queryScenarios: new Dictionary<string, FakeQueryScenario> { ["VALUES FIRST_LOB"] = first, ["VALUES SECOND_LOB"] = second });
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = new Db2iBatch(connection);
        batch.BatchCommands.Add(new Db2iBatchCommand("VALUES FIRST_LOB"));
        batch.BatchCommands.Add(new Db2iBatchCommand("VALUES SECOND_LOB"));
        await using var reader = await batch.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        using var stream = reader.GetStream(0);
        Assert.True(await reader.NextResultAsync());
        Assert.ThrowsAny<InvalidOperationException>(() => stream.ReadByte());
        Assert.Equal(new[] { 11, 12 }, server.FreedLobHandles.Order());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(new byte[] { 3 }, await reader.GetFieldValueAsync<byte[]>(0));
    }

    [Fact]
    public async Task BatchPreservesReadErrorWhenLocatorCleanupAlsoFails()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: BinaryQuery([1]) with
        { LobReadSqlCode = -443, LobFreeSqlCode = -999 });
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = new Db2iBatch(connection);
        var query = batch.BatchCommands.Add("VALUES BLOB_VALUE");
        batch.BatchCommands.Add("VALUES NOT_REACHED");
        await using var reader = await batch.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var error = await Assert.ThrowsAsync<Db2iException>(() => reader.GetFieldValueAsync<byte[]>(0));
        Assert.Equal(-443, error.SqlCode);
        Assert.Same(query, error.BatchCommand);
        Assert.True(reader.IsClosed);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Single(server.ExecutedSql);
    }

    [Fact]
    public async Task UndecodableLocatorBlockDiscardsThePooledSession()
    {
        var scenario = BinaryQuery([1]) with { Blocks = [[[0]]] };
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var source = new Db2iDataSource(ConnectionString(server) + ";Pooling=true;Maximum Pool Size=1");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = new Db2iCommand("VALUES BLOB_VALUE", connection))
        {
            var error = await Assert.ThrowsAsync<Db2iException>(() => command.ExecuteReaderAsync());
            Assert.Equal(Db2iErrorKind.Protocol, error.Kind);
        }
        await using var reopened = await source.OpenConnectionAsync();
        Assert.Equal(2, server.AcceptedConnectionCount);
    }

    [Fact]
    public async Task PooledDataSourceReusesSessionOnlyAfterReaderReleasesLocators()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: BinaryQuery([1, 2, 3]));
        await using var source = new Db2iDataSource(ConnectionString(server) + ";Pooling=true;Maximum Pool Size=1");
        Stream oldStream;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = new Db2iCommand("VALUES BLOB_VALUE", connection))
        {
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            oldStream = reader.GetStream(0);
            Assert.Equal(1, oldStream.ReadByte());
        }
        Assert.ThrowsAny<InvalidOperationException>(() => oldStream.ReadByte());
        Assert.Contains(11, server.FreedLobHandles);
        await using var reopened = await source.OpenConnectionAsync();
        await using var second = new Db2iCommand("VALUES BLOB_VALUE", reopened);
        Assert.Equal(new byte[] { 1, 2, 3 }, await second.ExecuteScalarAsync());
        Assert.Equal(1, server.AcceptedConnectionCount);
    }

    [Fact]
    public async Task ConnectionReplacementInvalidatesOldStreamWithoutAbortingNewSession()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: BinaryQuery([1, 2, 3]));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES BLOB_VALUE", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        using var oldStream = reader.GetStream(0);
        await connection.CloseAsync();
        await connection.OpenAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await oldStream.ReadAsync(new byte[1]));
        await using var nextCommand = new Db2iCommand("VALUES BLOB_VALUE", connection);
        Assert.Equal(new byte[] { 1, 2, 3 }, await nextCommand.ExecuteScalarAsync());
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ConnectionReplacementDuringUploadDoesNotAbortNewSession()
    {
        const string insert = "INSERT INTO LOBS(B) VALUES (?)";
        const string select = "VALUES BLOB_VALUE";
        await using var server = new FakeIbmIHostServer(queryScenarios: new Dictionary<string, FakeQueryScenario>
        {
            [insert] = InputScenario(960), [select] = BinaryQuery([1, 2, 3]),
        });
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand(insert, connection);
        using var input = new PausedInput(new byte[70_000]);
        command.Parameters.Add("b", input);
        var upload = command.ExecuteNonQueryAsync();
        await input.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await connection.CloseAsync();
            await connection.OpenAsync();
        }
        finally
        {
            input.Continue.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<Exception>(() => upload);
        await using var nextCommand = new Db2iCommand(select, connection);
        Assert.Equal(new byte[] { 1, 2, 3 }, await nextCommand.ExecuteScalarAsync());
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ReaderClosureAfterCommandDisposalReleasesLocatorsAndSessionLease()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: BinaryQuery([1, 2, 3]));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        var command = new Db2iCommand("VALUES BLOB_VALUE", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        using var stream = reader.GetStream(0);
        await command.DisposeAsync();
        await reader.CloseAsync();
        Assert.Contains(11, server.FreedLobHandles);
        Assert.ThrowsAny<InvalidOperationException>(() => stream.ReadByte());
        await using var nextCommand = new Db2iCommand("VALUES BLOB_VALUE", connection);
        Assert.Equal(new byte[] { 1, 2, 3 }, await nextCommand.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnverifiedLocatorReleaseDiscardsThePooledSession()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: BinaryQuery([1, 2, 3]) with { LobFreeSqlCode = -443 });
        await using var source = new Db2iDataSource(ConnectionString(server) + ";Pooling=true;Maximum Pool Size=1");
        await using (var connection = await source.OpenConnectionAsync())
        await using (var command = new Db2iCommand("VALUES BLOB_VALUE", connection))
        {
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var error = await Assert.ThrowsAsync<Db2iException>(() => reader.CloseAsync());
            Assert.Equal(-443, error.SqlCode);
        }

        await using var reopened = await source.OpenConnectionAsync();
        Assert.Equal(ConnectionState.Open, reopened.State);
        Assert.Equal(2, server.AcceptedConnectionCount);
    }

    private static Db2iFieldDescriptor Field(int type, string name, int offset, int ccsid = 65535, int locator = 0)
        => new(type | 1, 4, 0, 0, ccsid, name, offset, locator, 100_000_000);

    private static Db2iDataFormat EmptyFormat() => new(1, 0, 5, 2, []);

    private static FakeQueryScenario Query(IReadOnlyList<Db2iFieldDescriptor> fields,
        IReadOnlyList<IReadOnlyList<object?[]>> rows, IReadOnlyDictionary<int, FakeLobData> lobs)
        => new(new Db2iDataFormat(1, fields.Count * 4, 5, 2, fields), EmptyFormat(), rows, Lobs: lobs);

    private static FakeQueryScenario BinaryQuery(byte[] value)
        => Query([Field(960, "BLOB_VALUE", 0)], [[[11]]], new Dictionary<int, FakeLobData> { [11] = new(value) });

    private static FakeQueryScenario InputScenario(int type, int ccsid = 65535)
        => new(null, new Db2iDataFormat(1, 4, 5, 2, [Field(type, "INPUT", 0, ccsid, 21)]), [], RowsAffected: 1);

    private static string ConnectionString(FakeIbmIHostServer server)
        => $"Server=127.0.0.1;Port={server.Port};User ID=TESTUSER;Password=PASS123";

    private static Db2iConnection CreateConnection(FakeIbmIHostServer server) => new(ConnectionString(server));

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class NonSeekableInput(byte[] bytes) : Stream
    {
        public int BytesRead { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, bytes.Length - BytesRead);
            bytes.AsSpan(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class TrackingTextReader(string value) : TextReader
    {
        public int CharsRead { get; private set; }
        public bool WasDisposed { get; private set; }
        public override int Read(char[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));
        public override int Read(Span<char> buffer)
        {
            var count = Math.Min(buffer.Length, value.Length - CharsRead);
            value.AsSpan(CharsRead, count).CopyTo(buffer);
            CharsRead += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class PausedInput(byte[] bytes) : Stream
    {
        private int _offset;
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset > 0 && !Continue.Task.IsCompleted)
            {
                Paused.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
    }
}
