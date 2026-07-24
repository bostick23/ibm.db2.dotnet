using System.Collections;
using System.Data.Common;

namespace Db2i;

/// <summary>Contains the parameters associated with a <see cref="Db2iCommand"/>.</summary>
public sealed class Db2iParameterCollection : DbParameterCollection
{
    private readonly List<Db2iParameter> _items = [];

    public override int Count => _items.Count;

    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    public override int Add(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is not Db2iParameter parameter)
        {
            throw new ArgumentException($"Il parametro deve essere di tipo {nameof(Db2iParameter)}.", nameof(value));
        }

        _items.Add(parameter);
        return _items.Count - 1;
    }

    public Db2iParameter Add(string parameterName, object? value)
    {
        var parameter = new Db2iParameter(parameterName, value);
        Add(parameter);
        return parameter;
    }

    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => value is Db2iParameter parameter && _items.Contains(parameter);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => value is Db2iParameter parameter ? _items.IndexOf(parameter) : -1;

    public override int IndexOf(string parameterName)
    {
        ArgumentNullException.ThrowIfNull(parameterName);
        return _items.FindIndex(parameter =>
            string.Equals(parameter.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
    }

    public override void Insert(int index, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is not Db2iParameter parameter)
        {
            throw new ArgumentException($"Il parametro deve essere di tipo {nameof(Db2iParameter)}.", nameof(value));
        }

        _items.Insert(index, parameter);
    }

    public override void Remove(object value)
    {
        if (value is Db2iParameter parameter)
        {
            _items.Remove(parameter);
        }
    }

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new IndexOutOfRangeException($"Parametro '{parameterName}' non trovato.");
        }

        RemoveAt(index);
    }

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName)
    {
        var index = IndexOf(parameterName);
        return index >= 0
            ? _items[index]
            : throw new IndexOutOfRangeException($"Parametro '{parameterName}' non trovato.");
    }

    protected override void SetParameter(int index, DbParameter value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _items[index] = value as Db2iParameter
            ?? throw new ArgumentException($"Il parametro deve essere di tipo {nameof(Db2iParameter)}.", nameof(value));
    }

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new IndexOutOfRangeException($"Parametro '{parameterName}' non trovato.");
        }

        SetParameter(index, value);
    }
}
