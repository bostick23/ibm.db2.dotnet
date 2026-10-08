using System.Collections;
using System.Data;
using System.Data.Common;
using Db2i.Protocol;

namespace Db2i;

// Keeps at most one live result set. Closing the reader completes the remaining
// batch statements, unless execution failed or was canceled.
internal sealed class Db2iBatchDataReader(
    Db2iBatch batch,
    Db2iBatchCommand[] commands,
    CommandBehavior behavior) : DbDataReader
{
    private DbDataReader? _current;
    private int _nextCommand;
    private int _recordsAffected = -1;
    private bool _closed;
    private long _rowGeneration;

    private DbDataReader Current
    {
        get
        {
            EnsureOpen();
            return _current ?? throw new InvalidOperationException("Non è disponibile un result set.");
        }
    }

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override int Depth => 0;
    public override int FieldCount => _current?.FieldCount ?? 0;
    public override int VisibleFieldCount => _current?.VisibleFieldCount ?? 0;
    public override bool HasRows => _current?.HasRows ?? false;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => _recordsAffected;
    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);
    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => RunReaderOperationAsync(token => ((Db2iDataReader)Current).GetBytesAsync(
            ordinal, dataOffset, buffer, bufferOffset, length, token), CancellationToken.None)
            .GetAwaiter().GetResult();
    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => RunReaderOperationAsync(token => ((Db2iDataReader)Current).GetCharsAsync(
            ordinal, dataOffset, buffer, bufferOffset, length, token), CancellationToken.None)
            .GetAwaiter().GetResult();
    public override string GetDataTypeName(int ordinal) => Current.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => GetFieldValue<DateTime>(ordinal);
    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);
    public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);
    public override Type GetFieldType(int ordinal) => Current.GetFieldType(ordinal);
    public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);
    public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);
    public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);
    public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);
    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);
    public override string GetName(int ordinal) => Current.GetName(ordinal);
    public override int GetOrdinal(string name) => Current.GetOrdinal(name);
    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);
    public override object GetValue(int ordinal)
        => RunReaderOperationAsync(token => GetValueCoreAsync(ordinal, token), CancellationToken.None)
            .GetAwaiter().GetResult();
    internal Task<object> GetValueCoreAsync(int ordinal, CancellationToken token)
        => ((Db2iDataReader)Current).GetValueAsync(ordinal, token);
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, Current.FieldCount);
        for (var index = 0; index < count; index++) { values[index] = GetValue(index); }
        return count;
    }
    public override bool IsDBNull(int ordinal) => Current.IsDBNull(ordinal);
    public override T GetFieldValue<T>(int ordinal)
        => GetFieldValueAsync<T>(ordinal, CancellationToken.None).GetAwaiter().GetResult();
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (typeof(T) == typeof(Stream)) { return Task.FromResult((T)(object)GetStream(ordinal)); }
        if (typeof(T) == typeof(TextReader)) { return Task.FromResult((T)(object)GetTextReader(ordinal)); }
        return RunReaderOperationAsync(token => Current.GetFieldValueAsync<T>(ordinal, token), cancellationToken);
    }
    public override Stream GetStream(int ordinal)
    {
        var reader = Current;
        var generation = _rowGeneration;
        return new Db2iBatchLobStream(reader.GetStream(ordinal),
            (action, token) => RunLobReadAsync(reader, generation, action, token));
    }
    public override TextReader GetTextReader(int ordinal)
    {
        var reader = Current;
        var generation = _rowGeneration;
        return new Db2iBatchLobTextReader(reader.GetTextReader(ordinal),
            (action, token) => RunLobReadAsync(reader, generation, action, token));
    }
    private Task<int> RunLobReadAsync(DbDataReader reader, long generation,
        Func<CancellationToken, Task<int>> action, CancellationToken token)
    {
        EnsureOpen();
        if (!ReferenceEquals(_current, reader) || generation != _rowGeneration)
        {
            throw new InvalidOperationException("Lo stream appartiene a una riga o a un risultato precedente.");
        }
        return RunReaderOperationAsync(action, token);
    }
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
        => Current.IsDBNullAsync(ordinal, cancellationToken);
    public override DataTable? GetSchemaTable()
    {
        EnsureOpen();
        return _current?.GetSchemaTable();
    }
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        => RunReaderOperationAsync(ReadCoreAsync, cancellationToken);

    internal async Task<bool> ReadCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        batch.ValidateExecutionSession();
        _rowGeneration++;
        return _current is not null && await _current.ReadAsync(token).ConfigureAwait(false);
    }

    public override bool NextResult() => NextResultAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        => RunReaderOperationAsync(async token =>
        {
            if ((behavior & (CommandBehavior.SingleResult | CommandBehavior.SingleRow)) != 0)
            {
                while (await AdvanceCoreAsync(token).ConfigureAwait(false)) { }
                return false;
            }

            return await AdvanceCoreAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    internal async Task<bool> AdvanceCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        batch.ValidateExecutionSession();
        await CloseCurrentAsync(token).ConfigureAwait(false);
        while (_nextCommand < commands.Length)
        {
            token.ThrowIfCancellationRequested();
            batch.ValidateExecutionSession();
            var item = commands[_nextCommand++];
            batch.SetCurrentCommand(item);
            if (SqlProtocol.ClassifyStatement(item.CommandText) == Db2iStatementKind.Query)
            {
                _current = await item.Command.ExecuteReaderAsync(
                    behavior & ~CommandBehavior.CloseConnection, token).ConfigureAwait(false);
                return true;
            }

            if ((behavior & CommandBehavior.SchemaOnly) != 0)
            {
                await item.Command.PrepareAsync(token).ConfigureAwait(false);
            }
            else
            {
                var affected = await item.Command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                item.SetRecordsAffected(affected);
                if (affected >= 0)
                {
                    _recordsAffected = checked(Math.Max(0, _recordsAffected) + affected);
                }
            }
        }

        return false;
    }

    private async Task<T> RunReaderOperationAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        EnsureOpen();
        // Admission can fail while another operation owns the cursor. Only the
        // admitted operation may close it and release the batch on failure.
        var operation = batch.RunOperationAsync(ct =>
        {
            batch.ValidateExecutionSession();
            return action(ct);
        }, token);
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await AbortAsync().ConfigureAwait(false);
            }
            catch
            {
                // Locator cleanup already quarantines its session. Preserve the
                // original command error and its BatchCommand attribution.
            }
            throw;
        }
    }

    public override void Close() => CloseAsync().GetAwaiter().GetResult();

    public override async Task CloseAsync()
    {
        if (_closed) { return; }
        var operation = batch.RunOperationAsync(async token =>
        {
            while (await AdvanceCoreAsync(token).ConfigureAwait(false)) { }
            return true;
        }, CancellationToken.None);
        try
        {
            await operation.ConfigureAwait(false);
        }
        finally
        {
            await AbortAsync().ConfigureAwait(false);
        }
    }

    internal async ValueTask AbortAsync()
    {
        if (_closed) { return; }
        _closed = true;
        try
        {
            await CloseCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            await batch.CompleteAsync((behavior & CommandBehavior.CloseConnection) != 0).ConfigureAwait(false);
        }
    }

    private async ValueTask CloseCurrentAsync(CancellationToken cancellationToken = default)
    {
        _rowGeneration++;
        var current = _current;
        _current = null;
        if (current is not null)
        {
            if (!batch.HasExecutionSession && current is Db2iDataReader staleReader)
            {
                await staleReader.CloseAfterConnectionChangeAsync().ConfigureAwait(false);
            }
            else if (current is Db2iDataReader reader)
            {
                await reader.CloseWithCancellationAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await current.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Close(); }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void EnsureOpen()
    {
        if (_closed) { throw new InvalidOperationException("Il data reader è chiuso."); }
    }
}
