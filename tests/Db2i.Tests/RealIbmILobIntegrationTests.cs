using System.Data;

namespace Db2i.Tests;

public sealed class RealIbmILobIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task StreamsAndRollsBackLobsOnAnAuthorizedJournaledTable()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DB2I_TEST_LOB_ENABLED"), "true",
            StringComparison.OrdinalIgnoreCase)) { return; }
        var connectionString = Environment.GetEnvironmentVariable("DB2I_TEST_TCP_CONNECTION_STRING");
        var table = Environment.GetEnvironmentVariable("DB2I_TEST_LOB_TABLE");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(table)) { return; }
        Assert.Matches("^[A-Za-z_][A-Za-z0-9_#$@]*\\.[A-Za-z_][A-Za-z0-9_#$@]*$", table);
        table = table.ToUpperInvariant();
        var names = table.Split('.');
        await using var connection = new Db2iConnection(connectionString);
        await connection.OpenAsync();
        await ValidateTableAsync(connection, names[0], names[1]);
        var id = Guid.NewGuid().ToString("N");
        var bytes = new byte[17 * 1024 * 1024 + 7];
        for (var index = 0; index < bytes.Length; index++) { bytes[index] = (byte)(index % 251); }
        var text = new string('a', 70_000) + "  ";
        var unicode = new string('é', 40_000) + "€漢字  ";
        try
        {
            await using (var transaction = (Db2iTransaction)await connection.BeginTransactionAsync())
            {
                await using var batch = new Db2iBatch(connection, transaction);
                var insert = batch.BatchCommands.Add(
                    $"INSERT INTO {table} (ID, BLOB_VALUE, CLOB_VALUE, DBCLOB_VALUE) VALUES (?, ?, ?, ?)");
                insert.Parameters.Add("id", id);
                using var binaryInput = new MemoryStream(bytes, writable: false);
                using var textInput = new StringReader(text);
                using var unicodeInput = new StringReader(unicode);
                insert.Parameters.Add("binary", binaryInput);
                insert.Parameters.Add("text", textInput);
                insert.Parameters.Add("unicode", unicodeInput);
                await batch.PrepareAsync();
                Assert.Equal(0, binaryInput.Position);
                Assert.Equal(1, await batch.ExecuteNonQueryAsync());
                Assert.True(binaryInput.CanRead);

                await using var query = new Db2iCommand(
                    $"SELECT BLOB_VALUE, CLOB_VALUE, DBCLOB_VALUE FROM {table} WHERE ID = ?", connection)
                { Transaction = transaction };
                query.Parameters.Add("id", id);
                await using (var reader = await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess))
                {
                    Assert.True(await reader.ReadAsync());
                    var buffer = new byte[64 * 1024];
                    var position = 0;
                    await using (var stream = reader.GetStream(0))
                    {
                        int read;
                        while ((read = await stream.ReadAsync(buffer)) != 0)
                        {
                            Assert.True(position + read <= bytes.Length);
                            Assert.True(bytes.AsSpan(position, read).SequenceEqual(buffer.AsSpan(0, read)));
                            position += read;
                        }
                    }
                    Assert.Equal(bytes.Length, position);
                    using (var value = reader.GetTextReader(1))
                    { Assert.Equal(text, await value.ReadToEndAsync()); }
                    using (var value = reader.GetTextReader(2))
                    { Assert.Equal(unicode, await value.ReadToEndAsync()); }
                    Assert.False(await reader.ReadAsync());
                }
                await transaction.RollbackAsync();
            }
            await using var count = new Db2iCommand($"SELECT COUNT(*) FROM {table} WHERE ID = ?", connection);
            count.Parameters.Add("id", id);
            Assert.Equal(0, Convert.ToInt32(await count.ExecuteScalarAsync()));
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var cleanup = new Db2iCommand($"DELETE FROM {table} WHERE ID = ?", connection);
                cleanup.Parameters.Add("id", id);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task ValidateTableAsync(Db2iConnection connection, string schema, string table)
    {
        await using (var metadata = new Db2iCommand(
            "SELECT COLUMN_NAME, DATA_TYPE, LENGTH, CCSID FROM QSYS2.SYSCOLUMNS " +
            "WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ? ORDER BY ORDINAL_POSITION", connection))
        {
            metadata.Parameters.Add("schema", schema);
            metadata.Parameters.Add("table", table);
            await using var reader = await metadata.ExecuteReaderAsync();
            var columns = new List<(string Name, string Type, long Length, int Ccsid)>();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0).Trim(), reader.GetString(1).Trim(),
                    reader.GetInt64(2), reader.IsDBNull(3) ? 0 : reader.GetInt32(3)));
            }
            Assert.Equal(4, columns.Count);
            Assert.Equal(["ID", "BLOB_VALUE", "CLOB_VALUE", "DBCLOB_VALUE"], columns.Select(x => x.Name));
            Assert.Contains(columns[0].Type, new[] { "VARCHAR", "CHAR" });
            Assert.True(columns[0].Length >= 32);
            Assert.Equal("BLOB", columns[1].Type);
            Assert.True(columns[1].Length >= 18 * 1024 * 1024);
            Assert.Equal("CLOB", columns[2].Type);
            Assert.Equal("DBCLOB", columns[3].Type);
            Assert.Contains(columns[3].Ccsid, new[] { 1200, 13488 });
            Assert.True(columns[2].Length >= 128 * 1024);
            Assert.True(columns[3].Length >= 128 * 1024);
        }
        await using var journaling = new Db2iCommand(
            "SELECT COUNT(*) FROM QSYS2.JOURNALED_OBJECTS " +
            "WHERE OBJECT_LIBRARY = ? AND OBJECT_NAME = ? AND OBJECT_TYPE = '*FILE'", connection);
        journaling.Parameters.Add("schema", schema);
        journaling.Parameters.Add("table", table);
        Assert.True(Convert.ToInt32(await journaling.ExecuteScalarAsync()) > 0);
    }
}
