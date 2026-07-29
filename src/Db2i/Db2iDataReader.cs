using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Db2i.Protocol;

namespace Db2i;

/// <summary>Reads a forward-only stream of rows returned by Db2 for IBM i.</summary>
public sealed class Db2iDataReader : DbDataReader
{
    private readonly IReadOnlyList<Db2iColumn> _columns;
    private readonly Db2iCommand? _command;
    private readonly Db2iConnection? _connection;
    private readonly Db2iQueryCursor? _cursor;
    private readonly CommandBehavior _behavior;
    private readonly int _commandTimeout;
    private Db2iSessionLease? _lease;
    private IReadOnlyList<object?[]> _currentRows;
    private object?[]? _currentRow;
    private int _rowIndex = -1;
    private int _rowsRead;
    private bool _closed;

    internal Db2iDataReader(
        IReadOnlyList<Db2iColumn> columns,
        IReadOnlyList<object?[]> rows,
        int recordsAffected = -1)
    {
        _columns = columns;
        _currentRows = rows;
        RecordsAffected = recordsAffected;
        HasRows = rows.Count > 0;
        ValidateRows(rows);
    }

    internal Db2iDataReader(
        Db2iCommand command,
        Db2iConnection connection,
        IReadOnlyList<Db2iColumn> columns,
        Db2iQueryCursor? cursor,
        Db2iSessionLease? lease,
        CommandBehavior behavior,
        int commandTimeout)
    {
        _command = command;
        _connection = connection;
        _columns = columns;
        _cursor = cursor;
        _lease = lease;
        _behavior = behavior;
        _commandTimeout = commandTimeout;
        _currentRows = cursor?.CurrentBlock.Rows ?? [];
        HasRows = _currentRows.Count > 0;
        RecordsAffected = -1;
        ValidateRows(_currentRows);
    }

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int Depth => 0;

    public override int FieldCount => _columns.Count;

    public override bool HasRows { get; }

    public override bool IsClosed => _closed;

    public override int RecordsAffected { get; }

    public override void Close()
        => CloseCoreAsync(skipCursorClose: false).AsTask().GetAwaiter().GetResult();

    public override Task CloseAsync()
        => CloseCoreAsync(skipCursorClose: false).AsTask();

    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    public override long GetBytes(
        int ordinal,
        long dataOffset,
        byte[]? buffer,
        int bufferOffset,
        int length)
        => CopyValue(
            GetFieldValue<byte[]>(ordinal),
            dataOffset,
            buffer,
            bufferOffset,
            length);

    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);

    public override long GetChars(
        int ordinal,
        long dataOffset,
        char[]? buffer,
        int bufferOffset,
        int length)
        => CopyValue(
            GetFieldValue<string>(ordinal).ToCharArray(),
            dataOffset,
            buffer,
            bufferOffset,
            length);

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
        return _currentRow![ordinal] ?? DBNull.Value;
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

    public override Task<bool> IsDBNullAsync(
        int ordinal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsDBNull(ordinal));
    }

    public override bool NextResult()
    {
        EnsureNotClosed();
        return false;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextResult());
    }

    public override bool Read()
        => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        EnsureNotClosed();
        if ((_behavior & CommandBehavior.SingleRow) != 0 && _rowsRead >= 1)
        {
            _currentRow = null;
            return false;
        }

        while (true)
        {
            if (_rowIndex + 1 < _currentRows.Count)
            {
                _rowIndex++;
                _currentRow = _currentRows[_rowIndex];
                _rowsRead++;
                return true;
            }

            _currentRow = null;
            if (_cursor is null || _cursor.EndOfData)
            {
                return false;
            }

            using var operation = _command!.CreateOperationCancellation(cancellationToken);
            try
            {
                var block = await _cursor.FetchAsync(operation.Token).ConfigureAwait(false);
                _currentRows = block.Rows;
                _rowIndex = -1;
                ValidateRows(_currentRows);
            }
            catch (OperationCanceledException exception)
            {
                var synchronized = _connection is not null
                    && await _connection.CancelPendingOperationAsync().ConfigureAwait(false);
                await CloseCoreAsync(skipCursorClose: true).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        synchronized
                            ? "La lettura SQL è stata annullata dal chiamante."
                            : "La lettura SQL è stata annullata e la connessione non ha potuto essere risincronizzata.",
                        exception,
                        cancellationToken);
                }

                if (operation.TimedOut)
                {
                    throw new Db2iException(
                        synchronized
                            ? "Timeout durante il fetch SQL su IBM i; il comando è stato annullato."
                            : "Timeout durante il fetch SQL su IBM i; la connessione è stata chiusa.",
                        Db2iErrorKind.Timeout,
                        exception,
                        isTransient: true);
                }

                if (operation.ManuallyCanceled)
                {
                    throw new OperationCanceledException(
                        synchronized
                            ? "La lettura SQL è stata annullata tramite Cancel."
                            : "La lettura SQL è stata annullata e la connessione non ha potuto essere risincronizzata.",
                        exception);
                }

                throw;
            }
            catch (Exception exception)
                when (exception is IOException or ObjectDisposedException)
            {
                if (_connection is not null)
                {
                    await _connection.AbortCommandAsync().ConfigureAwait(false);
                }

                await CloseCoreAsync(skipCursorClose: true).ConfigureAwait(false);
                throw new Db2iException(
                    "La connessione IBM i si è interrotta durante il fetch SQL.",
                    Db2iErrorKind.Connection,
                    exception,
                    isTransient: true);
            }
        }
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

    public override Task<T> GetFieldValueAsync<T>(
        int ordinal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetFieldValue<T>(ordinal));
    }

    public override DataTable GetSchemaTable()
    {
        var schema = new DataTable("SchemaTable");
        schema.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        schema.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        schema.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
        schema.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
        schema.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        schema.Columns.Add("DataTypeName", typeof(string));
        schema.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));

        for (var ordinal = 0; ordinal < _columns.Count; ordinal++)
        {
            var column = _columns[ordinal];
            schema.Rows.Add(
                column.Name,
                ordinal,
                column.ColumnSize,
                checked((short)column.NumericPrecision),
                checked((short)column.NumericScale),
                column.FieldType,
                column.DataTypeName,
                column.AllowNull);
        }

        return schema;
    }

    public override IEnumerator GetEnumerator()
        => new DbEnumerator(this, closeReader: false);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseCoreAsync(skipCursorClose: false).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask CloseCoreAsync(bool skipCursorClose)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Exception? closeException = null;
        var leaseHeld = _lease is not null;
        try
        {
            if (!skipCursorClose && _cursor is not null)
            {
                await _cursor.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
            when (exception is IOException
                or ObjectDisposedException
                or InvalidOperationException)
        {
            closeException = exception;
            if (_connection is not null)
            {
                await _connection.AbortCommandAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (_command is not null)
            {
                try
                {
                    await _command.ReaderClosedAsync(
                            this,
                            CancellationToken.None,
                            leaseHeld)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                    when (exception is IOException
                        or ObjectDisposedException
                        or InvalidOperationException)
                {
                    closeException ??= exception;
                }
            }

            _lease?.Dispose();
            _lease = null;

            if ((_behavior & CommandBehavior.CloseConnection) != 0
                && _connection is not null)
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
        }

        if (closeException is not null && _connection?.State == ConnectionState.Open)
        {
            throw new Db2iException(
                "IBM i non ha completato la chiusura del cursore SQL.",
                Db2iErrorKind.Connection,
                closeException,
                isTransient: true);
        }
    }

    private static long CopyValue<T>(
        T[] source,
        long dataOffset,
        T[]? buffer,
        int bufferOffset,
        int length)
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
            throw new ArgumentException(
                "Il buffer di destinazione non è abbastanza grande.",
                nameof(buffer));
        }

        var count = (int)Math.Min(available, length);
        Array.Copy(source, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    private void ValidateRows(IReadOnlyList<object?[]> rows)
    {
        if (rows.Any(row => row.Length != _columns.Count))
        {
            throw new ArgumentException(
                "Ogni riga deve avere lo stesso numero di valori delle colonne.",
                nameof(rows));
        }
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
        if (_currentRow is null)
        {
            throw new InvalidOperationException(
                "Read deve essere chiamato prima di accedere ai valori.");
        }
    }

    private void EnsureOrdinal(int ordinal)
    {
        if ((uint)ordinal >= (uint)_columns.Count)
        {
            throw new IndexOutOfRangeException(
                $"Ordinale di colonna non valido: {ordinal}.");
        }
    }

}

internal sealed record Db2iColumn(
    string Name,
    Type FieldType,
    string DataTypeName,
    bool AllowNull,
    int ColumnSize = 0,
    int NumericPrecision = 0,
    int NumericScale = 0);
