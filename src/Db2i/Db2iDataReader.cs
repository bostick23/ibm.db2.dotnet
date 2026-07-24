using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Db2i;

/// <summary>Reads a forward-only stream of rows returned by Db2 for IBM i.</summary>
public sealed class Db2iDataReader : DbDataReader
{
    private readonly IReadOnlyList<Db2iColumn> _columns;
    private readonly IReadOnlyList<object?[]> _rows;
    private int _rowIndex = -1;
    private bool _closed;

    internal Db2iDataReader(IReadOnlyList<Db2iColumn> columns, IReadOnlyList<object?[]> rows, int recordsAffected = -1)
    {
        _columns = columns;
        _rows = rows;
        RecordsAffected = recordsAffected;

        if (_rows.Any(row => row.Length != _columns.Count))
        {
            throw new ArgumentException("Ogni riga deve avere lo stesso numero di valori delle colonne.", nameof(rows));
        }
    }

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int Depth => 0;

    public override int FieldCount => _columns.Count;

    public override bool HasRows => _rows.Count > 0;

    public override bool IsClosed => _closed;

    public override int RecordsAffected { get; }

    public override void Close() => _closed = true;

    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => CopyValue(GetFieldValue<byte[]>(ordinal), dataOffset, buffer, bufferOffset, length);

    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => CopyValue(GetFieldValue<string>(ordinal).ToCharArray(), dataOffset, buffer, bufferOffset, length);

    public override string GetDataTypeName(int ordinal)
    {
        EnsureOrdinal(ordinal);
        return _columns[ordinal].DataTypeName;
    }

    public override DateTime GetDateTime(int ordinal) => GetFieldValue<DateTime>(ordinal);

    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);

    public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);

    public override Type GetFieldType(int ordinal)
    {
        EnsureOrdinal(ordinal);
        return _columns[ordinal].FieldType;
    }

    public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);

    public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);

    public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);

    public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);

    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);

    public override string GetName(int ordinal)
    {
        EnsureOrdinal(ordinal);
        return _columns[ordinal].Name;
    }

    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var index = 0; index < _columns.Count; index++)
        {
            if (string.Equals(_columns[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new IndexOutOfRangeException($"Colonna '{name}' non trovata.");
    }

    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);

    public override object GetValue(int ordinal)
    {
        EnsureOnRow();
        EnsureOrdinal(ordinal);
        return _rows[_rowIndex][ordinal] ?? DBNull.Value;
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        EnsureOnRow();

        var count = Math.Min(values.Length, FieldCount);
        for (var index = 0; index < count; index++)
        {
            values[index] = GetValue(index);
        }

        return count;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override bool NextResult()
    {
        EnsureNotClosed();
        return false;
    }

    public override bool Read()
    {
        EnsureNotClosed();
        if (_rowIndex + 1 >= _rows.Count)
        {
            _rowIndex = _rows.Count;
            return false;
        }

        _rowIndex++;
        return true;
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is DBNull)
        {
            throw new InvalidCastException("La colonna contiene NULL.");
        }

        if (value is T typed)
        {
            return typed;
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    public override DataTable GetSchemaTable()
    {
        var schema = new DataTable("SchemaTable");
        schema.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        schema.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        schema.Columns.Add("DataTypeName", typeof(string));
        schema.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));

        for (var ordinal = 0; ordinal < _columns.Count; ordinal++)
        {
            var column = _columns[ordinal];
            schema.Rows.Add(column.Name, ordinal, column.FieldType, column.DataTypeName, column.AllowNull);
        }

        return schema;
    }

    public override IEnumerator GetEnumerator()
        => new DbEnumerator(this, closeReader: false);

    private static long CopyValue<T>(T[] source, long dataOffset, T[]? buffer, int bufferOffset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (dataOffset >= source.LongLength)
        {
            return 0;
        }

        var available = source.LongLength - dataOffset;
        if (buffer is null)
        {
            return available;
        }

        if (bufferOffset > buffer.Length || length > buffer.Length - bufferOffset)
        {
            throw new ArgumentException("Il buffer di destinazione non è abbastanza grande.", nameof(buffer));
        }

        var count = (int)Math.Min(available, length);
        Array.Copy(source, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    private void EnsureNotClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException("Il data reader è chiuso.");
        }
    }

    private void EnsureOnRow()
    {
        EnsureNotClosed();
        if (_rowIndex < 0 || _rowIndex >= _rows.Count)
        {
            throw new InvalidOperationException("Read deve essere chiamato prima di accedere ai valori.");
        }
    }

    private void EnsureOrdinal(int ordinal)
    {
        if ((uint)ordinal >= (uint)_columns.Count)
        {
            throw new IndexOutOfRangeException($"Ordinale di colonna non valido: {ordinal}.");
        }
    }
}

internal sealed record Db2iColumn(string Name, Type FieldType, string DataTypeName, bool AllowNull);
