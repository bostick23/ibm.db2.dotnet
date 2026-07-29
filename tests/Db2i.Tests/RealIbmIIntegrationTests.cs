namespace Db2i.Tests;

public sealed class RealIbmIIntegrationTests
{
    public static IEnumerable<object[]> ConfiguredConnections()
    {
        yield return ["DB2I_TEST_TCP_CONNECTION_STRING"];
        yield return ["DB2I_TEST_TLS_CONNECTION_STRING"];
    }

    [Theory]
    [MemberData(nameof(ConfiguredConnections))]
    [Trait("Category", "Integration")]
    public async Task OpensAndClosesAConfiguredIbmISystem(string environmentVariable)
    {
        var connectionString = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var connection = new Db2iConnection(connectionString);

        await connection.OpenAsync(CancellationToken.None);

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.True(connection.ServerCcsid > 0);
        Assert.NotEmpty(connection.ServerVersion);
        Assert.NotEmpty(connection.ServerJobIdentifier);

        await connection.CloseAsync();
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReusesTheSameRealIbmIJobFromTheGlobalPool()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "DB2I_TEST_TCP_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new Db2iConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MaxPoolSize = 1,
        };
        await using var first = new Db2iConnection(builder.ConnectionString);
        await using var second = new Db2iConnection(builder.ConnectionString);
        Db2iConnection.ClearPool(first);

        await first.OpenAsync();
        var jobIdentifier = first.ServerJobIdentifier;
        await first.CloseAsync();
        await second.OpenAsync();

        Assert.Equal(jobIdentifier, second.ServerJobIdentifier);
        await second.CloseAsync();
        Db2iConnection.ClearPool(second);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecutesReadOnlyM2QueriesAgainstConfiguredIbmI()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "DB2I_TEST_TCP_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var connection = new Db2iConnection(connectionString);
        await connection.OpenAsync();

        await using (var command = new Db2iCommand(
            """
            SELECT
                CAST(7 AS SMALLINT) AS SMALL_VALUE,
                CAST(42 AS INTEGER) AS INT_VALUE,
                CAST(9000000000 AS BIGINT) AS BIG_VALUE,
                CAST(123.45 AS DECIMAL(7, 2)) AS DEC_VALUE,
                CAST(1.25 AS REAL) AS REAL_VALUE,
                CAST(9.5 AS DOUBLE) AS DOUBLE_VALUE,
                CAST('ABC  ' AS CHAR(5)) AS FIXED_TEXT,
                CAST('citta' AS VARCHAR(10)) AS VAR_TEXT,
                DATE('2026-07-24') AS DATE_VALUE,
                TIME('11:22:33') AS TIME_VALUE,
                TIMESTAMP('2026-07-24-11.22.33.123456') AS TS_VALUE,
                CAST(X'010203' AS BINARY(3)) AS FIXED_BYTES,
                CAST(X'04050607' AS VARBINARY(4)) AS VAR_BYTES,
                CAST(NULL AS VARCHAR(10)) AS NULL_VALUE
            FROM SYSIBM.SYSDUMMY1
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal((short)7, reader.GetInt16(0));
            Assert.Equal(42, reader.GetInt32(1));
            Assert.Equal(9_000_000_000L, reader.GetInt64(2));
            Assert.Equal(123.45m, reader.GetDecimal(3));
            Assert.Equal(1.25f, reader.GetFloat(4));
            Assert.Equal(9.5d, reader.GetDouble(5));
            Assert.Equal("ABC  ", reader.GetString(6));
            Assert.Equal("citta", reader.GetString(7));
            Assert.Equal(new DateTime(2026, 7, 24), reader.GetDateTime(8));
            Assert.Equal(new TimeSpan(11, 22, 33), reader.GetFieldValue<TimeSpan>(9));
            Assert.Equal(
                new DateTime(2026, 7, 24, 11, 22, 33).AddTicks(1_234_560),
                reader.GetDateTime(10));
            Assert.Equal(new byte[] { 1, 2, 3 }, reader.GetFieldValue<byte[]>(11));
            Assert.Equal(new byte[] { 4, 5, 6, 7 }, reader.GetFieldValue<byte[]>(12));
            Assert.True(reader.IsDBNull(13));
            Assert.False(await reader.ReadAsync());
        }

        await using (var parameterCommand = new Db2iCommand(
            "VALUES CAST(? AS INTEGER)",
            connection))
        {
            parameterCommand.Parameters.Add("ignored", 73);
            Assert.Equal(73, await parameterCommand.ExecuteScalarAsync());
        }

        await using (var blockCommand = new Db2iCommand(
            """
            WITH RECURSIVE NUMBERS (N) AS
            (
                VALUES 1
                UNION ALL
                SELECT N + 1 FROM NUMBERS WHERE N < 5000
            )
            SELECT N FROM NUMBERS
            """,
            connection))
        await using (var reader = await blockCommand.ExecuteReaderAsync())
        {
            var count = 0;
            while (await reader.ReadAsync())
            {
                count++;
            }

            Assert.Equal(5000, count);
        }

        await using var invalidCommand = new Db2iCommand(
            "SELECT * FROM DB2I_M2_TABLE_THAT_DOES_NOT_EXIST",
            connection);
        var exception = await Assert.ThrowsAsync<Db2iException>(
            () => invalidCommand.ExecuteReaderAsync());
        Assert.Equal(Db2iErrorKind.Sql, exception.Kind);
        Assert.True(exception.SqlCode < 0);
        Assert.False(string.IsNullOrWhiteSpace(exception.SqlState));
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecutesAutocommitDmlAgainstAuthorizedM3Table()
    {
        var configuration = GetDmlConfiguration();
        if (configuration is null)
        {
            return;
        }

        await using var connection = new Db2iConnection(configuration.ConnectionString);
        await connection.OpenAsync();
        await AssertAuthorizedTableSignatureAsync(connection, configuration);
        var id = await FindUnusedIdAsync(connection, configuration);
        var marker = $"DB2I_M3_{Guid.NewGuid():N}";

        try
        {
            await using (var insert = new Db2iCommand(
                $"INSERT INTO {configuration.Table} " +
                $"({configuration.IdColumn}, {configuration.DescriptionColumn}, " +
                $"{configuration.DateColumn}, {configuration.DecimalColumn}) " +
                "VALUES (?, ?, ?, ?)",
                connection))
            {
                insert.Parameters.Add("id", id);
                insert.Parameters.Add("description", marker);
                insert.Parameters.Add("date", 26024);
                insert.Parameters.Add("value", 12.3456m);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            await using (var update = new Db2iCommand(
                $"UPDATE {configuration.Table} SET {configuration.DecimalColumn} = ? " +
                $"WHERE {configuration.IdColumn} = ?",
                connection))
            {
                update.Parameters.Add("value", 98.7654m);
                update.Parameters.Add("id", id);
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            }

            await using (var select = new Db2iCommand(
                $"SELECT {configuration.DescriptionColumn}, {configuration.DecimalColumn} " +
                $"FROM {configuration.Table} WHERE {configuration.IdColumn} = ?",
                connection))
            {
                select.Parameters.Add("id", id);
                await using var reader = await select.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(marker, reader.GetString(0).TrimEnd());
                Assert.Equal(98.7654m, reader.GetDecimal(1));
                Assert.False(await reader.ReadAsync());
            }

            await using (var delete = new Db2iCommand(
                $"DELETE FROM {configuration.Table} " +
                $"WHERE {configuration.IdColumn} = ?",
                connection))
            {
                delete.Parameters.Add("id", id);
                Assert.Equal(1, await delete.ExecuteNonQueryAsync());
            }
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await using var cleanup = new Db2iCommand(
                    $"DELETE FROM {configuration.Table} " +
                    $"WHERE {configuration.IdColumn} = ?",
                    connection);
                cleanup.Parameters.Add("id", id);
                _ = await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BeginsAndRollsBackEmptyTransactionAgainstIbmI()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "DB2I_TEST_TCP_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var connection = new Db2iConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("values")]
    [InlineData("table-count")]
    [InlineData("catalog-columns")]
    [InlineData("catalog-journal")]
    [Trait("Category", "Integration")]
    public async Task BeginsTransactionAfterReadOnlyStatement(string statement)
    {
        var configuration = statement == "values" ? null : GetDmlConfiguration();
        var connectionString = configuration?.ConnectionString
            ?? Environment.GetEnvironmentVariable("DB2I_TEST_TCP_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var sql = statement switch
        {
            "values" => "VALUES 1",
            "table-count" => $"SELECT COUNT(*) FROM {configuration!.Table}",
            "catalog-columns" =>
                "SELECT COUNT(*) FROM QSYS2.SYSCOLUMNS " +
                "WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ?",
            "catalog-journal" =>
                "SELECT COUNT(*) FROM QSYS2.JOURNALED_OBJECTS " +
                "WHERE OBJECT_LIBRARY = ? AND OBJECT_NAME = ? AND OBJECT_TYPE = '*FILE'",
            _ => throw new ArgumentOutOfRangeException(nameof(statement)),
        };

        await using var connection = new Db2iConnection(connectionString);
        await connection.OpenAsync();
        await using (var command = new Db2iCommand(sql, connection))
        {
            if (statement is "catalog-columns" or "catalog-journal")
            {
                command.Parameters.Add("schema", configuration!.Schema);
                command.Parameters.Add("table", configuration.TableName);
            }

            _ = await command.ExecuteScalarAsync();
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CommitsAndRollsBackAgainstJournaledAuthorizedM3Table()
    {
        var configuration = GetDmlConfiguration();
        if (configuration is null)
        {
            return;
        }

        await using var connection = new Db2iConnection(configuration.ConnectionString);
        await connection.OpenAsync();
        await AssertAuthorizedTableSignatureAsync(connection, configuration);
        if (!await IsJournaledAsync(connection, configuration))
        {
            Console.WriteLine(
                $"Test transazionale non eseguito: {configuration.Table} " +
                "non risulta journaled.");
            return;
        }

        var id = await FindUnusedIdAsync(connection, configuration);
        var marker = $"DB2I_M3_TX_{Guid.NewGuid():N}";
        try
        {
            var rollback = await connection.BeginTransactionAsync();
            await using (rollback)
            {
                await using var insert = CreateInsert(
                    connection,
                    rollback,
                    configuration,
                    id,
                    marker);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                await rollback.RollbackAsync();
            }

            Assert.Equal(0, await CountByIdAsync(connection, configuration, id));

            await using (var commit = await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable))
            {
                await using var insert = CreateInsert(
                    connection,
                    commit,
                    configuration,
                    id,
                    marker);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                await commit.CommitAsync();
            }

            Assert.Equal(1, await CountByIdAsync(connection, configuration, id));
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await using var cleanup = new Db2iCommand(
                    $"DELETE FROM {configuration.Table} " +
                    $"WHERE {configuration.IdColumn} = ?",
                    connection);
                cleanup.Parameters.Add("id", id);
                _ = await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PoolReturnRollsBackAgainstJournaledAuthorizedTable()
    {
        var configuration = GetDmlConfiguration();
        if (configuration is null)
        {
            return;
        }

        var builder = new Db2iConnectionStringBuilder(configuration.ConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 1,
        };
        await using var first = new Db2iConnection(builder.ConnectionString);
        await using var second = new Db2iConnection(builder.ConnectionString);
        Db2iConnection.ClearPool(first);
        await first.OpenAsync();
        await AssertAuthorizedTableSignatureAsync(first, configuration);
        if (!await IsJournaledAsync(first, configuration))
        {
            Console.WriteLine(
                $"Test pooling transazionale non eseguito: {configuration.Table} " +
                "non risulta journaled.");
            return;
        }

        var id = await FindUnusedIdAsync(first, configuration);
        var marker = $"DB2I_M4_POOL_{Guid.NewGuid():N}";
        try
        {
            var jobIdentifier = first.ServerJobIdentifier;
            await using var transaction = await first.BeginTransactionAsync();
            await using (var insert = CreateInsert(
                first,
                transaction,
                configuration,
                id,
                marker))
            {
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            await first.CloseAsync();
            await second.OpenAsync();

            Assert.Equal(jobIdentifier, second.ServerJobIdentifier);
            Assert.Equal(0, await CountByIdAsync(second, configuration, id));
        }
        finally
        {
            if (second.State == System.Data.ConnectionState.Open)
            {
                await using var cleanup = new Db2iCommand(
                    $"DELETE FROM {configuration.Table} " +
                    $"WHERE {configuration.IdColumn} = ?",
                    second);
                cleanup.Parameters.Add("id", id);
                _ = await cleanup.ExecuteNonQueryAsync();
                await second.CloseAsync();
            }

            Db2iConnection.ClearPool(second);
        }
    }

    private static DmlConfiguration? GetDmlConfiguration()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("DB2I_TEST_DML_ENABLED")?.Trim(),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var connectionString = GetRequiredEnvironmentVariable(
            "DB2I_TEST_TCP_CONNECTION_STRING");
        var table = GetRequiredEnvironmentVariable("DB2I_TEST_DML_TABLE");
        var tableParts = table.Split('.');
        if (tableParts.Length != 2
            || !IsSqlIdentifier(tableParts[0])
            || !IsSqlIdentifier(tableParts[1]))
        {
            throw new InvalidOperationException(
                "DB2I_TEST_DML_TABLE deve essere un identificatore a due parti " +
                "nel formato SCHEMA.TABLE.");
        }

        var columns = new[]
        {
            GetRequiredSqlIdentifier("DB2I_TEST_DML_ID_COLUMN"),
            GetRequiredSqlIdentifier("DB2I_TEST_DML_DESCRIPTION_COLUMN"),
            GetRequiredSqlIdentifier("DB2I_TEST_DML_DATE_COLUMN"),
            GetRequiredSqlIdentifier("DB2I_TEST_DML_DECIMAL_COLUMN"),
        };
        if (columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length)
        {
            throw new InvalidOperationException(
                "Le colonne della tabella DML di test devono essere distinte.");
        }

        return new DmlConfiguration(
            connectionString,
            tableParts[0].ToUpperInvariant(),
            tableParts[1].ToUpperInvariant(),
            columns[0],
            columns[1],
            columns[2],
            columns[3]);
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"La variabile di ambiente {name} è obbligatoria quando i test DML sono abilitati.");
        }

        return value.Trim();
    }

    private static string GetRequiredSqlIdentifier(string name)
    {
        var value = GetRequiredEnvironmentVariable(name);
        if (!IsSqlIdentifier(value))
        {
            throw new InvalidOperationException(
                $"{name} deve contenere un identificatore SQL semplice.");
        }

        return value.ToUpperInvariant();
    }

    private static bool IsSqlIdentifier(string value)
    {
        if (value.Length is < 1 or > 128 || !IsSqlIdentifierStart(value[0]))
        {
            return false;
        }

        return value.All(character =>
            IsAsciiLetter(character)
            || char.IsAsciiDigit(character)
            || character is '_' or '@' or '#' or '$');
    }

    private static bool IsSqlIdentifierStart(char character) =>
        IsAsciiLetter(character) || character is '_' or '@' or '#' or '$';

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static async Task AssertAuthorizedTableSignatureAsync(
        Db2iConnection connection,
        DmlConfiguration configuration)
    {
        await using var command = new Db2iCommand(
            """
            SELECT COLUMN_NAME, DATA_TYPE, LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, IS_NULLABLE
            FROM QSYS2.SYSCOLUMNS
            WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ?
            ORDER BY ORDINAL_POSITION
            """,
            connection);
        command.Parameters.Add("schema", configuration.Schema);
        command.Parameters.Add("table", configuration.TableName);
        var actual = new List<(string Name, string Type, long Length, int Precision, int Scale, string Nullable)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add((
                reader.GetString(0).Trim(),
                reader.GetString(1).Trim(),
                Convert.ToInt64(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(3)
                    ? 0
                    : Convert.ToInt32(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(4)
                    ? 0
                    : Convert.ToInt32(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(5).Trim()));
        }

        Assert.Equal(
        [
            (configuration.IdColumn, "DECIMAL", 5L, 5, 0, "N"),
            (configuration.DescriptionColumn, "CHAR", 100L, 0, 0, "N"),
            (configuration.DateColumn, "DECIMAL", 5L, 5, 0, "N"),
            (configuration.DecimalColumn, "DECIMAL", 9L, 9, 4, "N"),
        ],
            actual);
    }

    private static async Task<int> FindUnusedIdAsync(
        Db2iConnection connection,
        DmlConfiguration configuration)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var id = Random.Shared.Next(10_000, 99_999);
            if (await CountByIdAsync(connection, configuration, id) == 0)
            {
                return id;
            }
        }

        throw new InvalidOperationException(
            "Non è stato possibile trovare un ID libero nella tabella DML di test.");
    }

    private static async Task<int> CountByIdAsync(
        Db2iConnection connection,
        DmlConfiguration configuration,
        int id)
    {
        await using var command = new Db2iCommand(
            $"SELECT COUNT(*) FROM {configuration.Table} " +
            $"WHERE {configuration.IdColumn} = ?",
            connection);
        command.Parameters.Add("id", id);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> IsJournaledAsync(
        Db2iConnection connection,
        DmlConfiguration configuration)
    {
        await using var command = new Db2iCommand(
            """
            SELECT COUNT(*)
            FROM QSYS2.JOURNALED_OBJECTS
            WHERE OBJECT_LIBRARY = ? AND OBJECT_NAME = ? AND OBJECT_TYPE = '*FILE'
            """,
            connection);
        command.Parameters.Add("schema", configuration.Schema);
        command.Parameters.Add("table", configuration.TableName);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static Db2iCommand CreateInsert(
        Db2iConnection connection,
        System.Data.Common.DbTransaction transaction,
        DmlConfiguration configuration,
        int id,
        string marker)
    {
        var command = new Db2iCommand(
            $"INSERT INTO {configuration.Table} " +
            $"({configuration.IdColumn}, {configuration.DescriptionColumn}, " +
            $"{configuration.DateColumn}, {configuration.DecimalColumn}) " +
            "VALUES (?, ?, ?, ?)",
            connection)
        {
            Transaction = Assert.IsType<Db2iTransaction>(transaction),
        };
        command.Parameters.Add("id", id);
        command.Parameters.Add("description", marker);
        command.Parameters.Add("date", 26024);
        command.Parameters.Add("value", 12.3456m);
        return command;
    }

    private sealed record DmlConfiguration(
        string ConnectionString,
        string Schema,
        string TableName,
        string IdColumn,
        string DescriptionColumn,
        string DateColumn,
        string DecimalColumn)
    {
        public string Table => $"{Schema}.{TableName}";
    }
}
