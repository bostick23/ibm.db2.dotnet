using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Db2i;

/// <summary>Represents an SQL statement or stored procedure to execute on IBM i.</summary>
public sealed class Db2iCommand : DbCommand
{
    private readonly Db2iParameterCollection _parameters = new();
    private Db2iConnection? _connection;
    private Db2iTransaction? _transaction;

    public Db2iCommand()
    {
    }

    public Db2iCommand(string commandText, Db2iConnection connection)
    {
        CommandText = commandText;
        Connection = connection;
    }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.Both;

    public new Db2iConnection? Connection
    {
        get => _connection;
        set => _connection = value;
    }

    public new Db2iParameterCollection Parameters => _parameters;

    public new Db2iTransaction? Transaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value switch
        {
            null => null,
            Db2iConnection connection => connection,
            _ => throw new ArgumentException($"La connessione deve essere di tipo {nameof(Db2iConnection)}.", nameof(value)),
        };
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value switch
        {
            null => null,
            Db2iTransaction transaction => transaction,
            _ => throw new ArgumentException($"La transazione deve essere di tipo {nameof(Db2iTransaction)}.", nameof(value)),
        };
    }

    public override void Cancel()
    {
        EnsureCanExecute();
        throw Db2iConnection.FeatureNotImplemented("cancel SQL");
    }

    public override int ExecuteNonQuery()
    {
        EnsureCanExecute();
        throw Db2iConnection.FeatureNotImplemented("ExecuteNonQuery");
    }

    public override object? ExecuteScalar()
    {
        EnsureCanExecute();
        throw Db2iConnection.FeatureNotImplemented("ExecuteScalar");
    }

    public override void Prepare()
    {
        EnsureCanExecute();
        throw Db2iConnection.FeatureNotImplemented("prepare SQL");
    }

    protected override DbParameter CreateDbParameter() => new Db2iParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        EnsureCanExecute();
        throw Db2iConnection.FeatureNotImplemented("ExecuteReader");
    }

    private void EnsureCanExecute()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("Il comando non ha una connessione associata.");
        }

        _connection.EnsureOpen();
        if (string.IsNullOrWhiteSpace(CommandText))
        {
            throw new InvalidOperationException("CommandText non può essere vuoto.");
        }
    }
}
