using System.Buffers.Binary;
using System.Data;
using Db2i.Protocol;

namespace Db2i.Tests;

public sealed class Db2iDmlTransactionTests
{
    [Fact]
    public async Task ExecuteNonQueryReturnsSqlcaRowCountAndEncodesParameters()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: CreateFormat(("P1", 448, 22, 0, 20, 37)),
            Blocks: [],
            RowsAffected: 3);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand(
            "UPDATE TEST_TABLE SET VALUE = ?",
            connection);
        command.Parameters.Add("value", "updated");

        var affected = await command.ExecuteNonQueryAsync();

        Assert.Equal(3, affected);
        Assert.Equal((short)1, server.PreparedStatementType);
        Assert.Contains(SqlProtocol.Execute, server.SqlRequests);
        var data = Assert.IsType<ReadOnlyMemory<byte>>(server.LastParameterData);
        Assert.Equal("updated", IbmIEncoding.Decode(
            37,
            data.Span.Slice(24, BinaryPrimitives.ReadUInt16BigEndian(data.Span[22..]))));
    }

    [Fact]
    public async Task ExecuteNonQuerySurfacesSqlErrorsWithoutClosingTheConnection()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: [],
            ExecuteSqlCode: -530,
            ExecuteSqlState: "23503",
            ErrorMessage: "Vincolo referenziale violato");
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("DELETE FROM TEST_TABLE", connection);

        var exception = await Assert.ThrowsAsync<Db2iException>(
            () => command.ExecuteNonQueryAsync());

        Assert.Equal(-530, exception.SqlCode);
        Assert.Equal("23503", exception.SqlState);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task CallerCancellationUsesHostCancelAndKeepsTheConnectionReusable()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: [],
            RowsAffected: 1,
            StallOnExecute: true);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("UPDATE TEST_TABLE SET VALUE = 1", connection);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => command.ExecuteNonQueryAsync(cancellation.Token));

        Assert.Equal("123456/TESTUSER/QZDASOINIT", server.CancelTargetJobIdentifier);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task CancelSignalsTheActiveCommandAndIsANoOpWhenIdle()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: [],
            RowsAffected: 1,
            StallOnExecute: true);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("DELETE FROM TEST_TABLE", connection);

        command.Cancel();
        var execution = command.ExecuteNonQueryAsync();
        await WaitForRequestAsync(server, SqlProtocol.Execute);
        command.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task FailedHostCancelClosesTheUnsynchronizedConnection()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: [],
            StallOnExecute: true,
            CancelReturnCode: -1);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("DELETE FROM TEST_TABLE", connection);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => command.ExecuteNonQueryAsync(cancellation.Token));

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task CommandTimeoutUsesHostCancelAndKeepsTheConnectionReusable()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: [],
            RowsAffected: 1,
            StallOnExecute: true);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("UPDATE TEST_TABLE SET VALUE = 1", connection)
        {
            CommandTimeout = 1,
        };

        var exception = await Assert.ThrowsAsync<Db2iException>(
            () => command.ExecuteNonQueryAsync());

        Assert.Equal(Db2iErrorKind.Timeout, exception.Kind);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task QueryAndNonQueryExecutionMethodsCannotBeMixed()
    {
        var queryScenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [[[1]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: queryScenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand("VALUES 1", connection);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.ExecuteNonQueryAsync());

        Assert.DoesNotContain(SqlProtocol.PrepareDescribe, server.SqlRequests);
    }

    [Fact]
    public async Task CommitRestoresAutocommitAndCompletesTheTransaction()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: [],
            RowsAffected: 1);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted);
        await using var command = new Db2iCommand("DELETE FROM TEST_TABLE", connection)
        {
            Transaction = Assert.IsType<Db2iTransaction>(transaction),
        };

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();

        Assert.Contains(SqlProtocol.Commit, server.SqlRequests);
        Assert.Equal(
            [(false, (short)1), (true, (short)0)],
            server.TransactionModes);
        Assert.Null(Assert.IsType<Db2iTransaction>(transaction).Connection);
    }

    [Fact]
    public async Task ActiveTransactionMustBeAssignedToEveryCommand()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: []);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new Db2iCommand("UPDATE TEST_TABLE SET VALUE = 1", connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.ExecuteNonQueryAsync());

        Assert.Contains("stessa Db2iTransaction", exception.Message, StringComparison.Ordinal);
        await transaction.RollbackAsync();
        Assert.Contains(SqlProtocol.Rollback, server.SqlRequests);
    }

    [Fact]
    public async Task OnlyOneLocalTransactionCanBeActive()
    {
        var scenario = new FakeQueryScenario(null, EmptyFormat(), []);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.BeginTransactionAsync());

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task TransactionModeRetriesWithoutLocatorPersistenceWhenServerRejectsIt()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: [],
            RejectFirstLocatorPersistenceChange: true);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        Assert.Equal(
            [(false, (short)1), (false, (short)1)],
            server.TransactionModes);
        Assert.Equal([(short?)1, null], server.TransactionLocatorPersistence);

        await transaction.RollbackAsync();
        Assert.Equal(
            [(short?)1, null, null],
            server.TransactionLocatorPersistence);
    }

    [Theory]
    [InlineData(IsolationLevel.Unspecified, IsolationLevel.ReadCommitted, 1)]
    [InlineData(IsolationLevel.ReadUncommitted, IsolationLevel.ReadUncommitted, 2)]
    [InlineData(IsolationLevel.ReadCommitted, IsolationLevel.ReadCommitted, 1)]
    [InlineData(IsolationLevel.RepeatableRead, IsolationLevel.RepeatableRead, 3)]
    [InlineData(IsolationLevel.Serializable, IsolationLevel.Serializable, 4)]
    public async Task IsolationLevelsMapToIbmICommitmentControl(
        IsolationLevel requested,
        IsolationLevel effective,
        int commitmentLevel)
    {
        var scenario = new FakeQueryScenario(null, EmptyFormat(), []);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync(requested);

        Assert.Equal(effective, transaction.IsolationLevel);
        Assert.Equal(
            (false, (short)commitmentLevel),
            Assert.Single(server.TransactionModes));
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(IsolationLevel.Chaos)]
    [InlineData(IsolationLevel.Snapshot)]
    public async Task UnsupportedIsolationLevelsAreRejected(IsolationLevel isolationLevel)
    {
        var scenario = new FakeQueryScenario(null, EmptyFormat(), []);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();

        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await connection.BeginTransactionAsync(isolationLevel));

        Assert.Empty(server.TransactionModes);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task DisposingAnActiveTransactionRollsItBack()
    {
        var scenario = new FakeQueryScenario(null, EmptyFormat(), []);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();

        await transaction.DisposeAsync();

        Assert.Contains(SqlProtocol.Rollback, server.SqlRequests);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task CommitSqlErrorLeavesTheTransactionAvailableForRollback()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: [],
            CommitSqlCode: -7008,
            CommitSqlState: "55019",
            ErrorMessage: "Commit non disponibile");
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var exception = await Assert.ThrowsAsync<Db2iException>(
            () => transaction.CommitAsync());

        Assert.Equal(-7008, exception.SqlCode);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.NotNull(Assert.IsType<Db2iTransaction>(transaction).Connection);
        await transaction.RollbackAsync();
        Assert.Null(Assert.IsType<Db2iTransaction>(transaction).Connection);
    }

    [Fact]
    public async Task ClosingTheConnectionInvalidatesTheActiveTransaction()
    {
        var scenario = new FakeQueryScenario(null, EmptyFormat(), []);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await connection.CloseAsync();

        Assert.Null(Assert.IsType<Db2iTransaction>(transaction).Connection);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync());
    }

    [Theory]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE")]
    [InlineData("CALL MYPROC()")]
    public async Task UnsupportedTransactionControlAndProceduresAreRejected(string sql)
    {
        var scenario = new FakeQueryScenario(null, EmptyFormat(), []);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var command = new Db2iCommand(sql, connection);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => command.ExecuteNonQueryAsync());
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

    private static async Task WaitForRequestAsync(
        FakeIbmIHostServer server,
        ushort requestId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.SqlRequests.Contains(requestId))
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
