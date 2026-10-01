using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using Db2i.Protocol;

namespace Db2i.Tests;

public sealed class Db2iBatchTests
{
    [Fact]
    public void FactoryAndConnectionExposeTheStandardBatchSurface()
    {
        DbProviderFactory factory = Db2iProviderFactory.Instance;
        Assert.True(factory.CanCreateBatch);
        using var batch = Assert.IsType<Db2iBatch>(factory.CreateBatch());
        var command = Assert.IsType<Db2iBatchCommand>(factory.CreateBatchCommand());
        Assert.Equal(-1, command.RecordsAffected);
        Assert.True(command.CanCreateParameter);
        Assert.IsType<Db2iParameter>(command.CreateParameter());
        Assert.IsType<Db2iBatchCommand>(batch.CreateBatchCommand());

        using DbConnection connection = new Db2iConnection();
        Assert.True(connection.CanCreateBatch);
        using var connectedBatch = Assert.IsType<Db2iBatch>(connection.CreateBatch());
        Assert.Same(connection, connectedBatch.Connection);
    }

    [Fact]
    public void CollectionPreservesOrderAndRejectsDuplicateCommands()
    {
        using var batch = new Db2iBatch();
        var first = new Db2iBatchCommand("VALUES 1");
        var second = new Db2iBatchCommand("VALUES 2");
        batch.BatchCommands.Add(first);
        batch.BatchCommands.Insert(0, second);

        Assert.Same(second, batch.BatchCommands[0]);
        Assert.Same(first, batch.BatchCommands[1]);
        Assert.Throws<ArgumentException>(() => batch.BatchCommands.Add(first));
        Assert.Throws<ArgumentException>(() => batch.BatchCommands[0] = first);
        Assert.Throws<ArgumentNullException>(() => batch.BatchCommands.Add((DbBatchCommand)null!));
        var copy = new DbBatchCommand[2];
        batch.BatchCommands.CopyTo(copy, 0);
        Assert.Equal([second, first], copy);
        Assert.True(batch.BatchCommands.Remove(first));
        Assert.Same(second, Assert.Single(batch.BatchCommands));
    }

    [Fact]
    public async Task CommandOwnershipPersistsAfterRemovalAndProtectsOtherBatchesFromDisposal()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(1));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var first = new Db2iBatch(connection);
        await using var second = new Db2iBatch(connection);
        var original = new Db2iBatchCommand("DELETE FROM TEST_TABLE");
        var independent = new Db2iBatchCommand("DELETE FROM TEST_TABLE");
        first.BatchCommands.Add(original);
        second.BatchCommands.Add(independent);
        await first.PrepareAsync();
        await second.PrepareAsync();

        Assert.Throws<ArgumentException>(() => second.BatchCommands.Add(original));
        Assert.True(first.BatchCommands.Remove(original));
        Assert.Throws<ArgumentException>(() => second.BatchCommands.Insert(0, original));
        first.BatchCommands.Add(original);
        first.BatchCommands.Clear();
        Assert.Throws<ArgumentException>(() => second.BatchCommands[0] = original);
        first.BatchCommands.Add(original);
        Assert.Same(original, Assert.Single(first.BatchCommands));
        Assert.Same(independent, Assert.Single(second.BatchCommands));

        await first.DisposeAsync();

        Assert.Equal(1, await second.ExecuteNonQueryAsync());
        Assert.Equal(1, independent.RecordsAffected);
        Assert.Equal(2, server.PreparedStatements.Count);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ExecuteNonQueryRunsMixedStatementsInOrderAndAggregatesDmlCounts()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["UPDATE TEST_TABLE SET VALUE = 1"] = Dml(2),
            ["VALUES 42"] = Query(42) with { Blocks = [[[42]], [[43]]] },
            ["DELETE FROM TEST_TABLE"] = Dml(3),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);

        Assert.Equal(5, await batch.ExecuteNonQueryAsync());

        Assert.Equal(scenarios.Keys, server.ExecutedSql);
        Assert.Equal(2, batch.BatchCommands[0].RecordsAffected);
        Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected);
        Assert.Equal(3, batch.BatchCommands[2].RecordsAffected);
        Assert.Empty(server.TransactionModes);
        Assert.DoesNotContain(SqlProtocol.Commit, server.SqlRequests);
    }

    [Fact]
    public async Task QueryOnlyExecuteNonQueryReturnsMinusOne()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Query(1));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, ["VALUES 1", "VALUES 2"]);

        Assert.Equal(-1, await batch.ExecuteNonQueryAsync());
        Assert.Equal(2, server.ExecutedSql.Count);
        Assert.All(batch.BatchCommands, command => Assert.Equal(-1, command.RecordsAffected));
    }

    [Fact]
    public async Task DmlOnlyReaderCompletesBatchWithoutAResultSet()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(2));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection,
            ["UPDATE TEST_TABLE SET VALUE = 1", "DELETE FROM TEST_TABLE"]);
        await using var reader = await batch.ExecuteReaderAsync();

        Assert.Equal(4, reader.RecordsAffected);
        Assert.Equal(2, server.ExecutedSql.Count);
        Assert.Equal(0, reader.FieldCount);
        Assert.False(reader.HasRows);
        Assert.Null(reader.GetSchemaTable());
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        await reader.CloseAsync();
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteScalarUsesOnlyTheFirstQueryAndStillExecutesEveryCommand(bool emptyFirstQuery)
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["UPDATE TEST_TABLE SET VALUE = 1"] = Dml(1),
            ["VALUES 42"] = emptyFirstQuery ? Query(42) with { Blocks = [] } : Query(42),
            ["VALUES 99"] = Query(99),
            ["DELETE FROM TEST_TABLE"] = Dml(2),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);

        var result = await batch.ExecuteScalarAsync();

        if (emptyFirstQuery)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Equal(42, result);
        }

        Assert.Equal(scenarios.Keys, server.ExecutedSql);
        Assert.Equal(2, batch.BatchCommands[3].RecordsAffected);
    }

    [Fact]
    public async Task ReaderAdvancesAcrossQueriesAndExecutesInterveningDml()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["UPDATE TEST_TABLE SET VALUE = 1"] = Dml(2),
            ["VALUES 42"] = Query(42) with { Blocks = [[[42]], [[43]]] },
            ["DELETE FROM TEST_TABLE"] = Dml(3),
            ["VALUES 99"] = Query(99),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);
        await using var reader = await batch.ExecuteReaderAsync();

        Assert.Equal(1, reader.FieldCount);
        Assert.Equal(2, reader.RecordsAffected);
        Assert.Equal(2, server.ExecutedSql.Count);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(42, reader.GetInt32(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(43, reader.GetInt32(0));
        Assert.False(await reader.ReadAsync());
        Assert.True(await reader.NextResultAsync());
        Assert.Equal(5, reader.RecordsAffected);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(99, reader.GetInt32(0));
        Assert.False(await reader.NextResultAsync());
        Assert.Equal(scenarios.Keys, server.ExecutedSql);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task EarlyCloseCompletesPendingCommandsBeforeClosingTheConnection()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["VALUES 1"] = Query(1),
            ["DELETE FROM TEST_TABLE"] = Dml(4),
            ["VALUES 2"] = Query(2),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);
        await using var reader = await batch.ExecuteReaderAsync(CommandBehavior.CloseConnection);

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Single(server.ExecutedSql);
        await reader.CloseAsync();

        Assert.True(reader.IsClosed);
        Assert.Equal(scenarios.Keys, server.ExecutedSql);
        Assert.Equal(4, batch.BatchCommands[1].RecordsAffected);
        Assert.Equal(4, reader.RecordsAffected);
        Assert.Equal(ConnectionState.Closed, connection.State);
        await reader.CloseAsync();
        Assert.Equal(3, server.ExecutedSql.Count);
    }

    [Fact]
    public async Task CloseConnectionDoesNotCloseBetweenResultSets()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Query(1));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, ["VALUES 1", "VALUES 2"]);
        await using var reader = await batch.ExecuteReaderAsync(CommandBehavior.CloseConnection);

        Assert.True(await reader.NextResultAsync());
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.True(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        Assert.Equal(ConnectionState.Open, connection.State);
        await reader.CloseAsync();
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData("Exhausted")]
    [InlineData("ActiveCursor")]
    [InlineData("SchemaOnly")]
    public async Task ReopenedConnectionInvalidatesTheReaderWithoutRunningPendingCommandsOrClosingTheNewSession(
        string readerState)
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["VALUES 1"] = readerState == "ActiveCursor"
                ? Query(1) with { Blocks = [[[1]], [[2]]] }
                : Query(1),
            ["DELETE FROM TEST_TABLE"] = Dml(1),
            ["UPDATE TEST_TABLE SET VALUE = 3"] = Dml(3),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys.Take(2));
        var behavior = CommandBehavior.CloseConnection;
        if (readerState == "SchemaOnly")
        {
            behavior |= CommandBehavior.SchemaOnly;
        }

        await using var reader = await batch.ExecuteReaderAsync(behavior);
        Assert.Equal(readerState != "SchemaOnly", await reader.ReadAsync());
        if (readerState == "Exhausted")
        {
            Assert.False(await reader.ReadAsync());
        }

        await connection.CloseAsync();
        await connection.OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.NextResultAsync());

        Assert.True(reader.IsClosed);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.DoesNotContain("DELETE FROM TEST_TABLE", server.ExecutedSql);
        Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected);
        await using var command = new Db2iCommand("UPDATE TEST_TABLE SET VALUE = 3", connection);
        Assert.Equal(3, await command.ExecuteNonQueryAsync());
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Theory]
    [InlineData(CommandBehavior.SingleResult)]
    [InlineData(CommandBehavior.SingleRow)]
    public async Task SingleResultBehaviorStillExecutesRemainingStatements(CommandBehavior behavior)
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["VALUES 1"] = Query(1) with { Blocks = [[[1], [2]]] },
            ["DELETE FROM TEST_TABLE"] = Dml(5),
            ["VALUES 3"] = Query(3),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);
        await using var reader = await batch.ExecuteReaderAsync(behavior);

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        if (behavior == CommandBehavior.SingleRow)
        {
            Assert.False(await reader.ReadAsync());
        }

        Assert.False(await reader.NextResultAsync());
        Assert.Equal(scenarios.Keys, server.ExecutedSql);
        Assert.Equal(5, reader.RecordsAffected);
    }

    [Fact]
    public async Task SchemaOnlyTraversesQueryMetadataWithoutExecutingDmlOrOpeningCursors()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["DELETE FROM TEST_TABLE"] = Dml(3),
            ["VALUES 1"] = Query(1),
            ["VALUES 2"] = Query(2),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);
        await using var reader = await batch.ExecuteReaderAsync(CommandBehavior.SchemaOnly);

        Assert.Equal("VALUE", reader.GetName(0));
        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.False(await reader.ReadAsync());
        Assert.True(await reader.NextResultAsync());
        Assert.Equal(1, reader.FieldCount);
        Assert.False(await reader.NextResultAsync());
        Assert.Equal(scenarios.Keys, server.PreparedStatements);
        Assert.Empty(server.ExecutedSql);
        Assert.All(batch.BatchCommands, command => Assert.Equal(-1, command.RecordsAffected));
    }

    [Fact]
    public async Task PrepareCanBeReusedWithFreshParameterValuesAndIndependentStatements()
    {
        var scenario = Dml(1) with { ParameterFormat = IntegerFormat("P1") };
        await using var server = new FakeIbmIHostServer(queryScenario: scenario);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection,
            ["UPDATE TEST_TABLE SET VALUE = ?", "DELETE FROM TEST_TABLE WHERE VALUE = ?"]);
        var first = batch.BatchCommands[0].Parameters.Add("value", 11);
        var second = batch.BatchCommands[1].Parameters.Add("value", 22);

        await batch.PrepareAsync();
        Assert.Empty(server.ExecutedSql);
        Assert.Equal(2, server.PreparedStatements.Count);
        Assert.Equal(2, await batch.ExecuteNonQueryAsync());
        first.Value = 33;
        second.Value = 44;
        Assert.Equal(2, await batch.ExecuteNonQueryAsync());

        Assert.Equal(2, server.PreparedStatements.Count);
        Assert.Equal([11, 22, 33, 44], server.ExecutedParameters.Select(ReadInt32Parameter));
    }

    [Fact]
    public async Task SqlErrorIdentifiesTheOriginalCommandAndStopsTheBatch()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["UPDATE TEST_TABLE SET VALUE = 1"] = Dml(2),
            ["DELETE FROM TEST_TABLE"] = Dml(0) with
            {
                ExecuteSqlCode = -530,
                ExecuteSqlState = "23503",
                ErrorMessage = "Vincolo referenziale violato",
            },
            ["INSERT INTO TEST_TABLE VALUES (1)"] = Dml(1),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);

        var error = await Assert.ThrowsAsync<Db2iException>(() => batch.ExecuteNonQueryAsync());

        Assert.Same(batch.BatchCommands[1], error.BatchCommand);
        Assert.Equal(-530, error.SqlCode);
        Assert.Equal("23503", error.SqlState);
        Assert.Equal(scenarios.Keys.Take(2), server.ExecutedSql);
        Assert.Equal(2, batch.BatchCommands[0].RecordsAffected);
        Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected);
        Assert.Equal(-1, batch.BatchCommands[2].RecordsAffected);
        Assert.Empty(server.TransactionModes);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ReaderNextResultSurfacesTheFailedCommandAndDoesNotContinueOnDispose()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["VALUES 1"] = Query(1),
            ["VALUES 2"] = Query(2) with { PrepareSqlCode = -204, PrepareSqlState = "42704" },
            ["DELETE FROM TEST_TABLE"] = Dml(1),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);
        var reader = await batch.ExecuteReaderAsync();

        var error = await Assert.ThrowsAsync<Db2iException>(() => reader.NextResultAsync());
        Assert.Same(batch.BatchCommands[1], error.BatchCommand);
        await reader.DisposeAsync();

        Assert.Equal(["VALUES 1"], server.ExecutedSql);
        Assert.Equal(scenarios.Keys.Take(2), server.PreparedStatements);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("COMMIT")]
    [InlineData("CALL MYPROC()")]
    public async Task EveryCommandIsLocallyValidatedBeforeAnyStatementExecutes(string invalidSql)
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(1));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, ["DELETE FROM TEST_TABLE", invalidSql]);

        await Assert.ThrowsAnyAsync<Exception>(() => batch.ExecuteNonQueryAsync());

        Assert.Empty(server.PreparedStatements);
        Assert.Empty(server.ExecutedSql);
        Assert.All(batch.BatchCommands, command => Assert.Equal(-1, command.RecordsAffected));
    }

    [Fact]
    public async Task EmptyBatchIsRejected()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(1));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = new Db2iBatch(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteNonQueryAsync());
        Assert.Empty(server.PreparedStatements);
    }

    [Fact]
    public async Task ReaderPreventsCollectionChangesAndConcurrentBatchExecutionUntilClosed()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Query(1));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, ["VALUES 1"]);
        await using var reader = await batch.ExecuteReaderAsync();

        Assert.Throws<InvalidOperationException>(() => batch.BatchCommands.Add(new Db2iBatchCommand("VALUES 2")));
        Assert.Throws<InvalidOperationException>(() => batch.BatchCommands.Clear());
        Assert.Throws<InvalidOperationException>(() => batch.Timeout = 0);
        Assert.Throws<InvalidOperationException>(() => batch.Connection = null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteScalarAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        await reader.CloseAsync();

        batch.BatchCommands.Add(new Db2iBatchCommand("VALUES 2"));
        Assert.Equal(1, await batch.ExecuteScalarAsync());
        Assert.Equal(3, server.ExecutedSql.Count);
    }

    [Fact]
    public async Task ReexecutionResetsAllCountsIncludingCommandsSkippedAfterAnError()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["UPDATE TEST_TABLE SET VALUE = 1"] = Dml(1),
            ["UPDATE TEST_TABLE SET VALUE = 2"] = Dml(2),
            ["DELETE FROM TEST_TABLE"] = Dml(3),
            ["UPDATE MISSING_TABLE SET VALUE = 2"] = Dml(0) with
            {
                PrepareSqlCode = -204,
                PrepareSqlState = "42704",
            },
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys.Take(3));
        Assert.Equal(6, await batch.ExecuteNonQueryAsync());
        batch.BatchCommands[1].CommandText = "UPDATE MISSING_TABLE SET VALUE = 2";

        var error = await Assert.ThrowsAsync<Db2iException>(() => batch.ExecuteNonQueryAsync());

        Assert.Same(batch.BatchCommands[1], error.BatchCommand);
        Assert.Equal(1, batch.BatchCommands[0].RecordsAffected);
        Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected);
        Assert.Equal(-1, batch.BatchCommands[2].RecordsAffected);
        Assert.Equal(4, server.ExecutedSql.Count);
    }

    [Fact]
    public async Task OutputParametersAreRejectedBeforeAnyCommandExecutes()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(1));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection,
            ["DELETE FROM TEST_TABLE", "UPDATE TEST_TABLE SET VALUE = ?"]);
        batch.BatchCommands[1].Parameters.Add("value", 1).Direction = ParameterDirection.Output;

        await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteNonQueryAsync());

        Assert.Empty(server.PreparedStatements);
        Assert.Empty(server.ExecutedSql);
    }

    [Fact]
    public async Task ExplicitTransactionRemainsUnderCallerControl()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(1));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var transaction = Assert.IsType<Db2iTransaction>(
            await connection.BeginTransactionAsync());
        await using var batch = new Db2iBatch(connection, transaction);
        batch.BatchCommands.Add(new Db2iBatchCommand("UPDATE TEST_TABLE SET VALUE = 1"));
        batch.BatchCommands.Add(new Db2iBatchCommand("DELETE FROM TEST_TABLE"));

        Assert.Equal(2, await batch.ExecuteNonQueryAsync());
        Assert.Same(connection, transaction.Connection);
        Assert.DoesNotContain(SqlProtocol.Commit, server.SqlRequests);
        Assert.DoesNotContain(SqlProtocol.Rollback, server.SqlRequests);
        await transaction.RollbackAsync();
        Assert.Contains(SqlProtocol.Rollback, server.SqlRequests);
    }

    [Fact]
    public async Task ActiveTransactionMustBeAssignedToTheBatchBeforeExecution()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(1));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var batch = CreateBatch(connection, ["DELETE FROM TEST_TABLE"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteNonQueryAsync());
        Assert.Empty(server.PreparedStatements);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task DataSourceBatchCreatesCommandsAndRentsAndReturnsItsConnection()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(1));
        await using DbDataSource source = new Db2iDataSource(CreateConnectionString(server)
            + ";Pooling=true;Maximum Pool Size=1");
        await using var batch = source.CreateBatch();
        var command = batch.CreateBatchCommand();
        Assert.IsType<Db2iBatchCommand>(command);
        command.CommandText = "DELETE FROM TEST_TABLE";
        batch.BatchCommands.Add(command);

        Assert.Equal(1, await batch.ExecuteNonQueryAsync());
        Assert.Equal(1, await batch.ExecuteNonQueryAsync());
        await using var connection = await source.OpenConnectionAsync();
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(1, server.AcceptedConnectionCount);
    }

    [Fact]
    public async Task CancelStopsTheActiveStatementAndBatchCanBeReused()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["UPDATE TEST_TABLE SET VALUE = 1"] = Dml(1) with { StallOnExecute = true },
            ["DELETE FROM TEST_TABLE"] = Dml(2),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);
        batch.Cancel();
        var execution = batch.ExecuteNonQueryAsync();
        await WaitForAsync(() => server.ExecutedSql.Count == 1);

        batch.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Single(server.ExecutedSql);
        Assert.Equal("123456/TESTUSER/QZDASOINIT", server.CancelTargetJobIdentifier);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(3, await batch.ExecuteNonQueryAsync());
        Assert.Equal(1, batch.BatchCommands[0].RecordsAffected);
        Assert.Equal(2, batch.BatchCommands[1].RecordsAffected);
    }

    [Fact]
    public async Task CallerCancellationStopsPendingStatements()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(1) with { StallOnExecute = true });
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection,
            ["UPDATE TEST_TABLE SET VALUE = 1", "DELETE FROM TEST_TABLE"]);
        using var cancellation = new CancellationTokenSource();
        var execution = batch.ExecuteNonQueryAsync(cancellation.Token);
        await WaitForAsync(() => server.ExecutedSql.Count == 1);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Single(server.ExecutedSql);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task AlreadyCancelledExecutionDoesNotPrepareOrExecuteAnyCommand()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(1));
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, ["DELETE FROM TEST_TABLE"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => batch.ExecuteNonQueryAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(server.PreparedStatements);
        Assert.Empty(server.ExecutedSql);
        Assert.Equal(1, await batch.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task CancelInterruptsAnActiveReaderFetchAndDoesNotExecutePendingStatements()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["VALUES 1"] = Query(1) with { Blocks = [[[1]], [[2]]], StallOnFetch = true },
            ["DELETE FROM TEST_TABLE"] = Dml(1),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);
        await using var reader = await batch.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var read = reader.ReadAsync();
        await WaitForAsync(() => server.FetchCount == 1);

        batch.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(reader.IsClosed);
        Assert.Single(server.ExecutedSql);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("NextResult")]
    [InlineData("Close")]
    public async Task ConcurrentReaderOperationIsRejectedWithoutClosingTheActiveReader(string operation)
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["VALUES 1"] = Query(1) with { Blocks = [[[1]], [[2]]], StallOnFetch = true },
            ["DELETE FROM TEST_TABLE"] = Dml(1),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);
        await using var reader = await batch.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var read = reader.ReadAsync();
        await WaitForAsync(() => server.FetchCount == 1);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation switch
            {
                "Read" => reader.ReadAsync(),
                "NextResult" => reader.NextResultAsync(),
                "Close" => reader.CloseAsync(),
                _ => throw new InvalidOperationException(),
            });
            Assert.False(reader.IsClosed);
            Assert.False(read.IsCompleted);
        }
        finally
        {
            batch.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Single(server.ExecutedSql);
        Assert.Equal(ConnectionState.Open, connection.State);
        await using var command = new Db2iCommand("DELETE FROM TEST_TABLE", connection);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task TimeoutIdentifiesTheActiveCommandAndStopsPendingStatements()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(1) with { StallOnExecute = true });
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection,
            ["UPDATE TEST_TABLE SET VALUE = 1", "DELETE FROM TEST_TABLE"]);
        batch.Timeout = 1;

        var error = await Assert.ThrowsAsync<Db2iException>(() => batch.ExecuteNonQueryAsync());

        Assert.Equal(Db2iErrorKind.Timeout, error.Kind);
        Assert.Same(batch.BatchCommands[0], error.BatchCommand);
        Assert.Single(server.ExecutedSql);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ReaderReadTimeoutIdentifiesTheQueryAndStopsPendingStatements()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["VALUES 1"] = Query(1) with { Blocks = [[[1]], [[2]]], StallOnFetch = true },
            ["DELETE FROM TEST_TABLE"] = Dml(1),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);
        batch.Timeout = 1;
        await using var reader = await batch.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var error = await Assert.ThrowsAsync<Db2iException>(() => reader.ReadAsync());

        Assert.Equal(Db2iErrorKind.Timeout, error.Kind);
        Assert.Same(batch.BatchCommands[0], error.BatchCommand);
        Assert.Single(server.ExecutedSql);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task ReaderNextResultTimeoutIdentifiesTheNextCommand()
    {
        var scenarios = new Dictionary<string, FakeQueryScenario>
        {
            ["VALUES 1"] = Query(1),
            ["VALUES 2"] = Query(2) with { StallOnPrepare = true },
            ["DELETE FROM TEST_TABLE"] = Dml(1),
        };
        await using var server = new FakeIbmIHostServer(queryScenarios: scenarios);
        await using var connection = CreateConnection(server);
        await connection.OpenAsync();
        await using var batch = CreateBatch(connection, scenarios.Keys);
        batch.Timeout = 1;
        await using var reader = await batch.ExecuteReaderAsync();

        var error = await Assert.ThrowsAsync<Db2iException>(() => reader.NextResultAsync());

        Assert.Equal(Db2iErrorKind.Timeout, error.Kind);
        Assert.Same(batch.BatchCommands[1], error.BatchCommand);
        Assert.Single(server.ExecutedSql);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task SynchronousBatchExecutionUsesTheSameResultAndParameterContract()
    {
        await using var server = new FakeIbmIHostServer(queryScenario: Dml(2));
        using var connection = CreateConnection(server);
        connection.Open();
        using var batch = CreateBatch(connection, ["DELETE FROM TEST_TABLE"]);

        batch.Prepare();
        Assert.Equal(2, batch.ExecuteNonQuery());
        Assert.Equal(2, batch.BatchCommands[0].RecordsAffected);
        Assert.Single(server.PreparedStatements);
    }

    private static Db2iConnection CreateConnection(FakeIbmIHostServer server)
        => new(CreateConnectionString(server));

    private static string CreateConnectionString(FakeIbmIHostServer server)
        => $"Server=127.0.0.1;Port={server.Port};User ID=TESTUSER;Password=PASS123";

    private static Db2iBatch CreateBatch(Db2iConnection connection, IEnumerable<string> sql)
    {
        var batch = new Db2iBatch(connection);
        foreach (var statement in sql)
        {
            batch.BatchCommands.Add(new Db2iBatchCommand(statement));
        }

        return batch;
    }

    private static FakeQueryScenario Dml(int rowsAffected)
        => new(null, new Db2iDataFormat(1, 0, 5, 2, []), [], RowsAffected: rowsAffected);

    private static FakeQueryScenario Query(int value)
        => new(IntegerFormat("VALUE"), new Db2iDataFormat(1, 0, 5, 2, []), [[[value]]]);

    private static Db2iDataFormat IntegerFormat(string name)
        => new(1, 4, 5, 2, [new Db2iFieldDescriptor(497, 4, 0, 10, 0, name, 0)]);

    private static int ReadInt32Parameter(ReadOnlyMemory<byte>? data)
        => BinaryPrimitives.ReadInt32BigEndian(Assert.IsType<ReadOnlyMemory<byte>>(data).Span[22..]);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
