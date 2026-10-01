using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Db2i;

/// <summary>Represents one SQL statement in a <see cref="Db2iBatch"/>.</summary>
public sealed class Db2iBatchCommand : DbBatchCommand
{
    private int _recordsAffected = -1;
    private Db2iBatchCommandCollection? _owner;

    public Db2iBatchCommand()
    {
    }

    public Db2iBatchCommand(string commandText)
    {
        CommandText = commandText;
    }

    internal Db2iCommand Command { get; } = new();

    [AllowNull]
    public override string CommandText
    {
        get => Command.CommandText;
        set => Command.CommandText = value;
    }

    public override CommandType CommandType
    {
        get => Command.CommandType;
        set => Command.CommandType = value;
    }

    public override int RecordsAffected => _recordsAffected;

    public new Db2iParameterCollection Parameters => Command.Parameters;

    protected override DbParameterCollection DbParameterCollection => Parameters;

    public override bool CanCreateParameter => true;

    public override Db2iParameter CreateParameter() => new();

    internal void SetRecordsAffected(int value) => _recordsAffected = value;

    internal void AttachTo(Db2iBatchCommandCollection owner)
    {
        if (_owner is not null && !ReferenceEquals(_owner, owner))
        {
            throw new ArgumentException("Il comando appartiene già a un altro batch. Creare un nuovo Db2iBatchCommand.");
        }

        _owner = owner;
    }
}
