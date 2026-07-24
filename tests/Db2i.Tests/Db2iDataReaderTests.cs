namespace Db2i.Tests;

public sealed class Db2iDataReaderTests
{
    [Fact]
    public void ReadsRowsAndNulls()
    {
        var columns = new[]
        {
            new Db2iColumn("ID", typeof(int), "INTEGER", false),
            new Db2iColumn("NAME", typeof(string), "VARCHAR", true),
        };
        var rows = new object?[][]
        {
            [1, "Ada"],
            [2, null],
        };
        using var reader = new Db2iDataReader(columns, rows);

        Assert.True(reader.HasRows);
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Ada", reader["name"]);
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt32(reader.GetOrdinal("id")));
        Assert.True(reader.IsDBNull(1));
        Assert.False(reader.Read());
    }
}
