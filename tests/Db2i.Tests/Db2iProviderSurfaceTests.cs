using System.Data;
using System.Data.Common;

namespace Db2i.Tests;

public sealed class Db2iProviderSurfaceTests
{
    [Fact]
    public void FactoryCreatesProviderObjects()
    {
        Assert.IsType<Db2iConnection>(Db2iProviderFactory.Instance.CreateConnection());
        Assert.IsType<Db2iCommand>(Db2iProviderFactory.Instance.CreateCommand());
        Assert.IsType<Db2iParameter>(Db2iProviderFactory.Instance.CreateParameter());
    }

    [Fact]
    public void CreateCommandAssociatesTheConnection()
    {
        using var connection = new Db2iConnection("Server=my-system;User ID=MYUSER");

        using var command = connection.CreateCommand();

        Assert.Same(connection, command.Connection);
        Assert.IsAssignableFrom<DbCommand>(command);
    }

    [Fact]
    public void OpenFailsHonestlyUntilTheHandshakeIsImplemented()
    {
        using var connection = new Db2iConnection("Server=my-system;User ID=MYUSER;Password=secret");

        var exception = Assert.Throws<Db2iException>(() => connection.Open());

        Assert.Contains("handshake", exception.Message);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void ParametersImplementTheStandardContract()
    {
        using var command = new Db2iCommand();

        var parameter = command.Parameters.Add("p1", 42);

        Assert.Equal(DbType.Int32, parameter.DbType);
        Assert.Same(parameter, command.Parameters["P1"]);
        Assert.IsAssignableFrom<DbParameterCollection>(command.Parameters);
    }
}
