using System.Data;
using System.Data.Common;
using Db2i.Protocol;

namespace Db2i.Tests;

public sealed class Db2iPoolingTests
{
    [Fact]
    public async Task SequentialConnectionsReuseTheSamePhysicalSession()
    {
        await using var server = new FakeIbmIHostServer();
        var first = CreateConnection(server, aliases: false);
        var second = CreateConnection(server, aliases: true);
        await using (first)
        await using (second)
        {
            await first.OpenAsync();
            var firstJob = first.ServerJobIdentifier;
            await first.CloseAsync();

            await second.OpenAsync();

            Assert.Equal(firstJob, second.ServerJobIdentifier);
            Assert.Equal(1, server.AcceptedConnectionCount);
            await second.CloseAsync();
            Db2iConnection.ClearPool(second);
        }
    }

    [Fact]
    public async Task PoolingCanBeDisabled()
    {
        await using var server = new FakeIbmIHostServer();
        await using var first = CreateConnection(server, "Pooling=false");
        await using var second = CreateConnection(server, "Pooling=false");

        await first.OpenAsync();
        var firstJob = first.ServerJobIdentifier;
        await first.CloseAsync();
        await second.OpenAsync();

        Assert.NotEqual(firstJob, second.ServerJobIdentifier);
        Assert.Equal(2, server.AcceptedConnectionCount);
    }

    [Fact]
    public async Task DifferentEffectiveSettingsUseDifferentGlobalPools()
    {
        await using var server = new FakeIbmIHostServer();
        await using var first = CreateConnection(server, "Database=RDB_ONE");
        await using var second = CreateConnection(server, "Database=RDB_TWO");

        await first.OpenAsync();
        await first.CloseAsync();
        await second.OpenAsync();

        Assert.Equal(2, server.AcceptedConnectionCount);
        await second.CloseAsync();
        Db2iConnection.ClearPool(first);
        Db2iConnection.ClearPool(second);
    }

    [Fact]
    public async Task MaxPoolSizeWaitsAndUsesConnectTimeout()
    {
        await using var server = new FakeIbmIHostServer();
        await using var first = CreateConnection(server, "Max Pool Size=1;Connect Timeout=1");
        await using var timedOut = CreateConnection(server, "Max Pool Size=1;Connect Timeout=1");
        await first.OpenAsync();

        var exception = await Assert.ThrowsAsync<Db2iException>(
            () => timedOut.OpenAsync());

        Assert.Equal(Db2iErrorKind.Timeout, exception.Kind);
        Assert.True(exception.IsTransient);
        Assert.Equal(ConnectionState.Closed, timedOut.State);
        Assert.Equal(1, server.AcceptedConnectionCount);

        await using var waiting = CreateConnection(server, "Max Pool Size=1;Connect Timeout=0");
        var openTask = waiting.OpenAsync();
        await Task.Delay(50);
        Assert.Equal(ConnectionState.Connecting, waiting.State);

        var expectedJob = first.ServerJobIdentifier;
        await first.CloseAsync();
        await openTask;

        Assert.Equal(expectedJob, waiting.ServerJobIdentifier);
        Assert.Equal(1, server.MaximumActiveConnectionCount);
        await waiting.CloseAsync();
        Db2iConnection.ClearPool(waiting);
    }

    [Fact]
    public async Task CallerCancellationWhileWaitingDoesNotConsumeAPoolSlot()
    {
        await using var server = new FakeIbmIHostServer();
        await using var first = CreateConnection(server, "Max Pool Size=1;Connect Timeout=0");
        await using var canceled = CreateConnection(server, "Max Pool Size=1;Connect Timeout=0");
        await first.OpenAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.OpenAsync(cancellation.Token));

        await using var next = CreateConnection(server, "Max Pool Size=1;Connect Timeout=0");
        var nextOpen = next.OpenAsync();
        await first.CloseAsync();
        await nextOpen;

        Assert.Equal(ConnectionState.Open, next.State);
        Assert.Equal(1, server.AcceptedConnectionCount);
        await next.CloseAsync();
        Db2iConnection.ClearPool(next);
    }

    [Fact]
    public async Task ReturningATransactionRollsBackAndDeletesPreparedStatements()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: [],
            RowsAffected: 1);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var first = CreateConnection(server);
        await using var second = CreateConnection(server);
        await first.OpenAsync();
        var firstJob = first.ServerJobIdentifier;
        await using var transaction = (Db2iTransaction)await first.BeginTransactionAsync();
        await using var command = new Db2iCommand(
            "UPDATE TEST_TABLE SET VALUE = 1",
            first)
        {
            Transaction = transaction,
        };
        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        await first.CloseAsync();
        await second.OpenAsync();

        Assert.Equal(firstJob, second.ServerJobIdentifier);
        Assert.Contains(SqlProtocol.Rollback, server.SqlRequests);
        Assert.Contains(SqlProtocol.DeleteRpb, server.SqlRequests);
        Assert.Equal(
            [(false, (short)1), (true, (short)0)],
            server.TransactionModes);

        command.Transaction = null;
        command.Connection = second;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await second.CloseAsync();
        Db2iConnection.ClearPool(second);
    }

    [Fact]
    public async Task ClosingWithAnActiveReaderDiscardsThePhysicalSession()
    {
        var scenario = new FakeQueryScenario(
            CreateFormat(("VALUE", 496, 4, 0, 10, 0)),
            EmptyFormat(),
            [[[42]]]);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var first = CreateConnection(server);
        await first.OpenAsync();
        await using var command = new Db2iCommand("VALUES 42", first);
        await using var reader = await command.ExecuteReaderAsync();

        await first.CloseAsync();

        await using var second = CreateConnection(server);
        await second.OpenAsync();
        Assert.Equal(2, server.AcceptedConnectionCount);
        await second.CloseAsync();
        Db2iConnection.ClearPool(second);
    }

    [Fact]
    public async Task ClearPoolInvalidatesIdleAndLeasedSessions()
    {
        await using var server = new FakeIbmIHostServer();
        await using var idle = CreateConnection(server);
        await idle.OpenAsync();
        await idle.CloseAsync();

        Db2iConnection.ClearPool(idle);

        await using var leased = CreateConnection(server);
        await leased.OpenAsync();
        Assert.Equal(2, server.AcceptedConnectionCount);
        Db2iConnection.ClearPool(leased);
        await leased.CloseAsync();

        await using var afterClear = CreateConnection(server);
        await afterClear.OpenAsync();
        Assert.Equal(3, server.AcceptedConnectionCount);
        await afterClear.CloseAsync();
        Db2iConnection.ClearAllPools();
    }

    [Fact]
    public async Task DataSourceOwnsAnIsolatedPoolAndHasAnImmutableConnectionString()
    {
        await using var server = new FakeIbmIHostServer();
        var connectionString = CreateConnectionString(server);
        await using var firstSource = new Db2iDataSource(connectionString);
        await using var secondSource = new Db2iDataSource(connectionString);
        await using var first = await firstSource.OpenConnectionAsync();
        var firstJob = first.ServerJobIdentifier;
        await first.CloseAsync();

        await using var reused = await firstSource.OpenConnectionAsync();
        Assert.Equal(firstJob, reused.ServerJobIdentifier);
        await reused.CloseAsync();

        await using var isolated = await secondSource.OpenConnectionAsync();
        Assert.NotEqual(firstJob, isolated.ServerJobIdentifier);
        Assert.Equal(2, server.AcceptedConnectionCount);
        Assert.Throws<InvalidOperationException>(
            () => isolated.ConnectionString = connectionString + ";Database=OTHER");
    }

    [Fact]
    public async Task DisposedDataSourceRejectsNewAndPreviouslyCreatedClosedConnections()
    {
        await using var server = new FakeIbmIHostServer();
        var source = new Db2iDataSource(CreateConnectionString(server));
        await using var connection = source.CreateConnection();
        await source.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => source.CreateConnection());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.OpenAsync());
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task DisposingADataSourceDiscardsLeasedSessionsWhenTheyReturn()
    {
        await using var server = new FakeIbmIHostServer();
        var connectionString = CreateConnectionString(server);
        var source = new Db2iDataSource(connectionString);
        await using var leased = await source.OpenConnectionAsync();

        await source.DisposeAsync();
        Assert.Equal(ConnectionState.Open, leased.State);
        await leased.CloseAsync();

        await using var replacementSource = new Db2iDataSource(connectionString);
        await using var replacement = await replacementSource.OpenConnectionAsync();
        Assert.Equal(2, server.AcceptedConnectionCount);
    }

    [Fact]
    public async Task DataSourceCommandAutomaticallyRentsAndReturnsAConnection()
    {
        var scenario = new FakeQueryScenario(
            ResultFormat: null,
            ParameterFormat: EmptyFormat(),
            Blocks: [],
            RowsAffected: 2);
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using DbDataSource source = Db2iProviderFactory.Instance.CreateDataSource(
            CreateConnectionString(server));
        await using var command = source.CreateCommand("UPDATE TEST_TABLE SET VALUE = 2");

        Assert.Equal(2, await command.ExecuteNonQueryAsync());
        Assert.Equal(2, await command.ExecuteNonQueryAsync());

        Assert.Equal(1, server.AcceptedConnectionCount);
        Assert.Throws<NotSupportedException>(() => source.CreateBatch());
    }

    private static Db2iConnection CreateConnection(
        FakeIbmIHostServer server,
        string additionalSettings = "",
        bool aliases = false)
        => new(CreateConnectionString(server, additionalSettings, aliases));

    private static string CreateConnectionString(
        FakeIbmIHostServer server,
        string additionalSettings = "",
        bool aliases = false)
        => aliases
            ? $"Data Source=127.0.0.1;Port={server.Port};UID=TESTUSER;PWD=PASS123;{additionalSettings}"
            : $"Server=127.0.0.1;Port={server.Port};User ID=TESTUSER;Password=PASS123;{additionalSettings}";

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
}
