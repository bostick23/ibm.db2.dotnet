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

    private DbDataReader Current
    {
        get
        {
            EnsureOpen();
            return _current ?? throw new InvalidOperationException("Non è disponibile un result set.");
        }
    }

    public override object this[int ordinal] => Current[ordinal];
    public override object this[string name] => Current[name];
    public override int Depth => 0;
    public override int FieldCount => _current?.FieldCount ?? 0;
    public override int VisibleFieldCount => _current?.VisibleFieldCount ?? 0;
    public override bool HasRows => _current?.HasRows ?? false;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => _recordsAffected;
    public override bool GetBoolean(int ordinal) => Current.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => Current.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => Current.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => Current.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => Current.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override string GetDataTypeName(int ordinal) => Current.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => Current.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => Current.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => Current.GetDouble(ordinal);
    public override Type GetFieldType(int ordinal) => Current.GetFieldType(ordinal);
    public override float GetFloat(int ordinal) => Current.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => Current.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => Current.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => Current.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => Current.GetInt64(ordinal);
    public override string GetName(int ordinal) => Current.GetName(ordinal);
    public override int GetOrdinal(string name) => Current.GetOrdinal(name);
    public override string GetString(int ordinal) => Current.GetString(ordinal);
    public override object GetValue(int ordinal) => Current.GetValue(ordinal);
    public override int GetValues(object[] values) => Current.GetValues(values);
    public override bool IsDBNull(int ordinal) => Current.IsDBNull(ordinal);
    public override T GetFieldValue<T>(int ordinal) => Current.GetFieldValue<T>(ordinal);
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
        => Current.GetFieldValueAsync<T>(ordinal, cancellationToken);
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
        await CloseCurrentAsync().ConfigureAwait(false);
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

    private async Task<bool> RunReaderOperationAsync(Func<CancellationToken, Task<bool>> action, CancellationToken token)
    {
        EnsureOpen();
        // Admission can fail while another operation owns the cursor. Only the
        // admitted operation may close it and release the batch on failure.
        var operation = batch.RunOperationAsync(action, token);
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch
        {
            await AbortAsync().ConfigureAwait(false);
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

    private async ValueTask CloseCurrentAsync()
    {
        var current = _current;
        _current = null;
        if (current is not null)
        {
            if (!batch.HasExecutionSession && current is Db2iDataReader staleReader)
            {
                await staleReader.CloseAfterConnectionChangeAsync().ConfigureAwait(false);
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
