using System.Data.Common;

namespace Db2i;

/// <summary>Contains the ordered SQL statements in a <see cref="Db2iBatch"/>.</summary>
public sealed class Db2iBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<Db2iBatchCommand> _items = [];
    private readonly Action _ensureMutable;

    internal Db2iBatchCommandCollection(Action ensureMutable)
    {
        ArgumentNullException.ThrowIfNull(ensureMutable);
        _ensureMutable = ensureMutable;
    }

    public override int Count => _items.Count;

    public override bool IsReadOnly => false;

    public new Db2iBatchCommand this[int index]
    {
        get => _items[index];
        set => SetBatchCommand(index, value);
    }

    public void Add(Db2iBatchCommand item) => Add((DbBatchCommand)item);

    public Db2iBatchCommand Add(string commandText)
    {
        var command = new Db2iBatchCommand(commandText);
        Add(command);
        return command;
    }

    public override void Add(DbBatchCommand item)
    {
        _ensureMutable();
        var command = ValidateItem(item);
        EnsureNotPresent(command);
        command.AttachTo(this);
        _items.Add(command);
    }

    public override void Clear()
    {
        _ensureMutable();
        _items.Clear();
    }

    public override bool Contains(DbBatchCommand item)
        => item is Db2iBatchCommand command && _items.Contains(command);

    public override void CopyTo(DbBatchCommand[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (arrayIndex > array.Length || _items.Count > array.Length - arrayIndex)
        {
            throw new ArgumentException(
                "L'array di destinazione non è abbastanza grande.",
                nameof(array));
        }

        for (var index = 0; index < _items.Count; index++)
        {
            array[arrayIndex + index] = _items[index];
        }
    }

    public override IEnumerator<DbBatchCommand> GetEnumerator()
        => ((IEnumerable<DbBatchCommand>)_items).GetEnumerator();

    public override int IndexOf(DbBatchCommand item)
        => item is Db2iBatchCommand command ? _items.IndexOf(command) : -1;

    public override void Insert(int index, DbBatchCommand item)
    {
        _ensureMutable();
        var command = ValidateItem(item);
        EnsureNotPresent(command);
        if ((uint)index > (uint)_items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        command.AttachTo(this);
        _items.Insert(index, command);
    }

    public override bool Remove(DbBatchCommand item)
    {
        _ensureMutable();
        return item is Db2iBatchCommand command && _items.Remove(command);
    }

    public override void RemoveAt(int index)
    {
        _ensureMutable();
        _items.RemoveAt(index);
    }

    protected override DbBatchCommand GetBatchCommand(int index) => _items[index];

    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand)
    {
        _ensureMutable();
        var command = ValidateItem(batchCommand);
        var existing = _items[index];
        if (ReferenceEquals(existing, command))
        {
            return;
        }

        EnsureNotPresent(command);
        command.AttachTo(this);
        _items[index] = command;
    }

    private static Db2iBatchCommand ValidateItem(DbBatchCommand item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item as Db2iBatchCommand
            ?? throw new ArgumentException(
                $"Il comando deve essere di tipo {nameof(Db2iBatchCommand)}.",
                nameof(item));
    }

    private void EnsureNotPresent(Db2iBatchCommand command)
    {
        if (_items.Contains(command))
        {
            throw new ArgumentException(
                "Lo stesso comando non può comparire più volte nel batch.",
                nameof(command));
        }
    }
}
