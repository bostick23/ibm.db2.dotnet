using System.Data.Common;

namespace Db2i;

/// <summary>Creates provider-specific ADO.NET objects.</summary>
public sealed class Db2iProviderFactory : DbProviderFactory
{
    public static readonly Db2iProviderFactory Instance = new();

    private Db2iProviderFactory()
    {
    }

    public override DbConnection CreateConnection() => new Db2iConnection();

    public override DbCommand CreateCommand() => new Db2iCommand();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new Db2iConnectionStringBuilder();

    public override DbParameter CreateParameter() => new Db2iParameter();

    public override DbDataSource CreateDataSource(string connectionString)
        => new Db2iDataSource(connectionString);
}
