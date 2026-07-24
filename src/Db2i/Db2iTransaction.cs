using System.Data;
using System.Data.Common;

namespace Db2i;

/// <summary>Represents a transaction on a Db2 for IBM i connection.</summary>
public sealed class Db2iTransaction : DbTransaction
{
    private readonly Db2iConnection _connection;

    internal Db2iTransaction(Db2iConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    public override IsolationLevel IsolationLevel { get; }

    protected override DbConnection DbConnection => _connection;

    public override void Commit()
    {
        _connection.EnsureOpen();
        throw Db2iConnection.FeatureNotImplemented("commit");
    }

    public override void Rollback()
    {
        _connection.EnsureOpen();
        throw Db2iConnection.FeatureNotImplemented("rollback");
    }
}
