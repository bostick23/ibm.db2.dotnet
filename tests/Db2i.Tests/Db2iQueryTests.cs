using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using Db2i.Protocol;

namespace Db2i.Tests;

public sealed class Db2iQueryTests
{
    [Fact]
    public async Task EmptyExtendedResultPayloadRepresentsAnEmptyResultSet()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [],
            ReturnEmptyResultPayload: true);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand(
            "SELECT VALUE FROM TEST_TABLE WHERE 1 = 0",
            connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.False(await reader.ReadAsync());
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ExecuteReaderStreamsBlocksAndMapsTheM2Types()
    {
        var format = CreateFormat(
            ("SMALL_VALUE", 500, 2, 0, 5, 0),
            ("INT_VALUE", 496, 4, 0, 10, 0),
            ("BIG_VALUE", 492, 8, 0, 19, 0),
            ("DEC_VALUE", 484, 4, 2, 7, 0),
            ("REAL_VALUE", 480, 4, 0, 24, 0),
            ("DOUBLE_VALUE", 480, 8, 0, 53, 0),
            ("FIXED_TEXT", 452, 5, 0, 5, 280),
            ("VAR_TEXT", 448, 22, 0, 20, 280),
            ("DATE_VALUE", 384, 10, 0, 10, 280),
            ("TIME_VALUE", 388, 8, 0, 8, 280),
            ("TS_VALUE", 392, 26, 6, 26, 280),
            ("FIXED_BYTES", 912, 3, 0, 3, 0),
            ("VAR_BYTES", 908, 6, 0, 4, 0));
        var timestamp = new DateTime(2026, 7, 24, 11, 22, 33).AddTicks(1_234_560);
        var scenario = new FakeQueryScenario(
            format,
            EmptyFormat(),
            [
                [
                    [
                        (short)-12,
                        42,
                        9_000_000_000L,
                        12345.67m,
                        1.25f,
                        9.5d,
                        "CIAO ",
                        "città",
                        new DateTime(2026, 7, 24),
                        new TimeSpan(11, 22, 33),
                        timestamp,
                        new byte[] { 1, 2, 3 },
                        new byte[] { 4, 5, 6, 7 },
                    ],
                ],
                [
                    Enumerable.Repeat<object?>(null, format.Fields.Count).ToArray(),
                ],
            ]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand(
            "SELECT * FROM TEST_TYPES",
            connection);

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(reader.HasRows);
        Assert.True(await reader.ReadAsync());
        Assert.Equal((short)-12, reader.GetInt16(0));
        Assert.Equal(42, reader.GetInt32(reader.GetOrdinal("int_value")));
        Assert.Equal(9_000_000_000L, reader.GetInt64(2));
        Assert.Equal(12345.67m, reader.GetDecimal(3));
        Assert.Equal(1.25f, reader.GetFloat(4));
        Assert.Equal(9.5d, reader.GetDouble(5));
        Assert.Equal("CIAO ", reader.GetString(6));
        Assert.Equal("città", reader.GetString(7));
        Assert.Equal(new DateTime(2026, 7, 24), reader.GetDateTime(8));
        Assert.Equal(new TimeSpan(11, 22, 33), reader.GetFieldValue<TimeSpan>(9));
        Assert.Equal(timestamp, reader.GetDateTime(10));
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.GetFieldValue<byte[]>(11));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, reader.GetFieldValue<byte[]>(12));

        Assert.True(await reader.ReadAsync());
        Assert.All(
            Enumerable.Range(0, reader.FieldCount),
            ordinal => Assert.True(reader.IsDBNull(ordinal)));
        Assert.False(await reader.ReadAsync());
        Assert.Equal(1, server.FetchCount);

        var schema = Assert.IsType<DataTable>(reader.GetSchemaTable());
        Assert.Equal("DECIMAL", schema.Rows[3]["DataTypeName"]);
        Assert.Equal((short)7, schema.Rows[3][SchemaTableColumn.NumericPrecision]);
        Assert.Equal((short)2, schema.Rows[3][SchemaTableColumn.NumericScale]);
        Assert.Equal(typeof(TimeSpan), reader.GetFieldType(9));
        Assert.Equal("SELECT * FROM TEST_TYPES", server.PreparedSql);
    }

    [Fact]
    public async Task PositionalParameterUsesExplicitAdoNetDescriptorAndValue()
    {
        var result = CreateFormat(("ANSWER", 496, 4, 0, 10, 0));
        var marker = CreateFormat(("P1", 448, 130, 0, 128, 37));
        var scenario = new FakeQueryScenario(
            result,
            marker,
            [[[42]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES CAST(? AS INTEGER)", connection);
        command.Parameters.Add(new Db2iParameter("ignored_name", 42)
        {
            DbType = DbType.Int32,
        });

        var value = await command.ExecuteScalarAsync();

        Assert.Equal(42, value);
        var clientFormat = Assert.IsType<Db2iDataFormat>(server.LastClientParameterFormat);
        Assert.Equal(496, clientFormat.Fields[0].NativeType);
        Assert.Equal(4, clientFormat.Fields[0].Length);
        var data = Assert.IsType<ReadOnlyMemory<byte>>(server.LastParameterData);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(data.Span[4..]));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(data.Span[8..]));
        Assert.Equal(42, BinaryPrimitives.ReadInt32BigEndian(data.Span[22..]));
    }

    [Fact]
    public async Task NullWithoutExplicitTypeUsesServerDescriptor()
    {
        var result = CreateFormat(("VALUE", 496, 4, 0, 10, 0));
        var marker = CreateFormat(
            ("P1", 496, 4, 0, 10, 0),
            ("P2", 448, 12, 0, 10, 37));
        var scenario = new FakeQueryScenario(result, marker, [[[1]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES 1 WHERE ? IS NOT NULL OR ? IS NULL", connection);
        command.Parameters.Add("first", 7);
        command.Parameters.Add("second", DBNull.Value);

        _ = await command.ExecuteScalarAsync();

        var clientFormat = Assert.IsType<Db2iDataFormat>(server.LastClientParameterFormat);
        Assert.Equal(496, clientFormat.Fields[0].NativeType);
        Assert.Equal(448, clientFormat.Fields[1].NativeType);
        var data = Assert.IsType<ReadOnlyMemory<byte>>(server.LastParameterData);
        Assert.Equal(-1, BinaryPrimitives.ReadInt16BigEndian(data.Span[22..]));
    }

    [Fact]
    public async Task ExplicitTypeControlsTypedNull()
    {
        var result = CreateFormat(("VALUE", 496, 4, 0, 10, 0));
        var marker = CreateFormat(("P1", 448, 12, 0, 10, 37));
        var scenario = new FakeQueryScenario(result, marker, [[[1]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES COALESCE(?, 1)", connection);
        command.Parameters.Add(new Db2iParameter("p", DBNull.Value)
        {
            DbType = DbType.Int64,
        });

        _ = await command.ExecuteScalarAsync();

        var clientFormat = Assert.IsType<Db2iDataFormat>(server.LastClientParameterFormat);
        Assert.Equal(492, clientFormat.Fields[0].NativeType);
        var data = Assert.IsType<ReadOnlyMemory<byte>>(server.LastParameterData);
        Assert.Equal(-1, BinaryPrimitives.ReadInt16BigEndian(data.Span[20..]));
    }

    [Fact]
    public async Task MarkerCountMismatchFailsBeforeOpen()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            CreateFormat(("P1", 496, 4, 0, 10, 0)),
            [[[1]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES ?", connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.ExecuteScalarAsync());

        Assert.Contains("1 marker", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.DoesNotContain(SqlProtocol.OpenDescribeFetch, server.SqlRequests);
    }

    [Fact]
    public async Task SqlErrorIncludesSqlCodeStateAndServerText()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [],
            PrepareSqlCode: -204,
            PrepareSqlState: "42704",
            ErrorMessage: "Oggetto SQL non trovato");
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("SELECT * FROM MISSING_TABLE", connection);

        var exception = await Assert.ThrowsAsync<Db2iException>(
            () => command.ExecuteReaderAsync());

        Assert.Equal(Db2iErrorKind.Sql, exception.Kind);
        Assert.Equal(-204, exception.SqlCode);
        Assert.Equal("42704", exception.SqlState);
        Assert.Contains("Oggetto SQL non trovato", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ActiveReaderExclusivelyOwnsTheConnectionStream()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [[[1]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var first = new Db2iCommand("VALUES 1", connection);
        await using var second = new Db2iCommand("VALUES 2", connection);
        await using var reader = await first.ExecuteReaderAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.ExecuteReaderAsync());

        Assert.Contains("data reader attivo", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaOnlyReturnsMetadataWithoutOpeningACursor()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [[[1]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES 1", connection);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SchemaOnly | CommandBehavior.CloseConnection);

        Assert.Equal(1, reader.FieldCount);
        Assert.False(reader.HasRows);
        Assert.False(await reader.ReadAsync());
        await reader.CloseAsync();
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.DoesNotContain(SqlProtocol.OpenDescribeFetch, server.SqlRequests);
    }

    [Fact]
    public async Task SingleRowStopsAfterTheFirstRow()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [[
                [1],
                [2],
            ]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES 1, 2", connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task CancellationDuringFetchKeepsTheConnectionSynchronized()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [
                [[1]],
                [[2]],
            ],
            StallOnFetch: true);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("SELECT VALUE FROM MANY_ROWS", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(cancellation.Token));

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal("123456/TESTUSER/QZDASOINIT", server.CancelTargetJobIdentifier);
    }

    [Fact]
    public async Task CancellationDuringPrepareDrainsAndDeletesTheIncompleteStatement()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [[[7]]],
            StallOnPrepare: true);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES 7", connection);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => command.ExecuteScalarAsync(cancellation.Token));

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(7, await command.ExecuteScalarAsync());
        Assert.Contains(SqlProtocol.DeleteRpb, server.SqlRequests);
    }

    [Fact]
    public async Task UnsupportedBehaviorAndParameterDirectionAreRejected()
    {
        var result = CreateFormat(("VALUE", 496, 4, 0, 10, 0));
        var marker = CreateFormat(("P1", 496, 4, 0, 10, 0));
        var scenario = new FakeQueryScenario(result, marker, [[[1]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES ?", connection);

        Assert.Throws<NotSupportedException>(
            () => command.ExecuteReader(CommandBehavior.KeyInfo));

        command.Parameters.Add(new Db2iParameter("p", 1)
        {
            Direction = ParameterDirection.Output,
        });
        await Assert.ThrowsAsync<NotSupportedException>(
            () => command.ExecuteReaderAsync());
    }

    [Fact]
    public async Task PreparedStatementIsReusedAcrossExecutions()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [[[11]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES 11", connection);

        await command.PrepareAsync();
        Assert.Equal(11, await command.ExecuteScalarAsync());
        Assert.Equal(11, await command.ExecuteScalarAsync());

        Assert.Equal(1, server.SqlRequests.Count(value => value == SqlProtocol.CreateRpb));
        Assert.Equal(1, server.SqlRequests.Count(value => value == SqlProtocol.PrepareDescribe));
        Assert.Equal(2, server.SqlRequests.Count(value => value == SqlProtocol.OpenDescribeFetch));
    }

    [Fact]
    public async Task ParameterSizeAndScaleNeverSilentlyTruncate()
    {
        var result = CreateFormat(("VALUE", 496, 4, 0, 10, 0));
        var marker = CreateFormat(("P1", 448, 22, 0, 20, 37));
        var scenario = new FakeQueryScenario(result, marker, [[[1]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();

        await using (var stringCommand = new Db2iCommand("VALUES LENGTH(?)", connection))
        {
            stringCommand.Parameters.Add(new Db2iParameter("p", "troppo lungo")
            {
                Size = 3,
            });
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => stringCommand.ExecuteScalarAsync());
            Assert.Contains("Size=3", exception.Message, StringComparison.Ordinal);
        }

        await using (var decimalCommand = new Db2iCommand("VALUES CAST(? AS INTEGER)", connection))
        {
            decimalCommand.Parameters.Add(new Db2iParameter("p", 1.234m)
            {
                DbType = DbType.Decimal,
                Precision = 3,
                Scale = 2,
            });
            await Assert.ThrowsAsync<ArgumentException>(
                () => decimalCommand.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task TimeParameterMustFitWithinOneDay()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            CreateFormat(("P1", 388, 8, 0, 8, 37)),
            [[[1]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES ?", connection);
        command.Parameters.Add(new Db2iParameter("p", TimeSpan.FromDays(1))
        {
            DbType = DbType.Time,
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => command.ExecuteScalarAsync());
    }

    [Fact]
    public void SynchronousReaderUsesTheSameQueryPath()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [[[99]]]);
        using var server = new SyncDisposableFakeServer(scenario);
        using var connection = CreateConnection(server.Server);
        connection.Open();
        using var command = new Db2iCommand("VALUES 99", connection);
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(99, reader.GetInt32(0));
        Assert.False(reader.Read());
    }

    private static Db2iConnection CreateConnection(FakeIbmIHostServer server)
        => new(
            $"Server=127.0.0.1;Port={server.Port};User ID=TESTUSER;Password=PASS123");

    private static Db2iDataFormat EmptyFormat()
        => new(1, 0, 5, 2, []);

    private static Db2iDataFormat CreateFormat(
        params (string Name, int Type, int Length, int Scale, int Precision, int Ccsid)[] columns)
    {
        var fields = new List<Db2iFieldDescriptor>(columns.Length);
        var offset = 0;
        foreach (var column in columns)
        {
            fields.Add(new Db2iFieldDescriptor(
                column.Type | 1,
                column.Length,
                column.Scale,
                column.Precision,
                column.Ccsid,
                column.Name,
                offset));
            offset += column.Length;
        }

        return new Db2iDataFormat(1, offset, 5, 2, fields);
    }

    private sealed class SyncDisposableFakeServer : IDisposable
    {
        internal SyncDisposableFakeServer(FakeQueryScenario scenario)
        {
            Server = new FakeIbmIHostServer(queryScenario: scenario);
        }

        internal FakeIbmIHostServer Server { get; }

        public void Dispose()
            => Server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
