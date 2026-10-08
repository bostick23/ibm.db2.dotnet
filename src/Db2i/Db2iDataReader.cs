using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Db2i.Protocol;

namespace Db2i;

/// <summary>Reads a forward-only stream of rows returned by Db2 for IBM i.</summary>
public sealed class Db2iDataReader : DbDataReader
{
    private readonly IReadOnlyList<Db2iColumn> _columns;
    private readonly Db2iCommand? _command;
    private readonly Db2iConnection? _connection;
    private readonly Db2iQueryCursor? _cursor;
    private readonly Db2iSession? _session;
    private readonly CommandBehavior _behavior;
    private readonly int _commandTimeout;
    private Db2iSessionLease? _lease;
    private IReadOnlyList<object?[]> _currentRows;
    private object?[]? _currentRow;
    private int _rowIndex = -1;
    private int _rowsRead;
    private bool _closed;
    private bool _exhausted;
    private int _operationActive;
    private int _valueGeneration;
    private int _sequentialOrdinal = -1;
    private long _sequentialOffset;
    private int _partialOrdinal = -1;
    private Db2iLobReadState? _partialBytes;
    private Db2iLobTextState? _partialChars;

    internal const int LobChunkSize = 64 * 1024;
    internal const int LobMaterializationLimit = 64 * 1024 * 1024;

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
        _session = cursor?.Session ?? connection.GetOpenSession();
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
        => CloseAsync().GetAwaiter().GetResult();

    public override Task CloseAsync()
        => CloseWithCancellationAsync(CancellationToken.None);

    internal ValueTask CloseAfterConnectionChangeAsync()
        => CloseCoreAsync(skipCursorClose: true);

    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    public override long GetBytes(
        int ordinal,
        long dataOffset,
        byte[]? buffer,
        int bufferOffset,
        int length)
        => GetBytesAsync(ordinal, dataOffset, buffer, bufferOffset, length, CancellationToken.None)
            .GetAwaiter().GetResult();

    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);

    public override long GetChars(
        int ordinal,
        long dataOffset,
        char[]? buffer,
        int bufferOffset,
        int length)
        => GetCharsAsync(ordinal, dataOffset, buffer, bufferOffset, length, CancellationToken.None)
            .GetAwaiter().GetResult();

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
        => GetValueAsync(ordinal, CancellationToken.None).GetAwaiter().GetResult();

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

    public override bool IsDBNull(int ordinal)
    {
        var value = GetRawValue(ordinal);
        // Checking the null indicator does not consume data in the current
        // column, but still respects the forward-only order of columns.
        EnsureValueAccess(ordinal, ordinal == _sequentialOrdinal ? _sequentialOffset : 0);
        return value is DBNull;
    }

    public override Task<bool> IsDBNullAsync(
        int ordinal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsDBNull(ordinal));
    }

    public override bool NextResult()
        => NextResultAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        => RunOperationAsync(async token =>
        {
            InvalidateValueAccess();
            _currentRow = null;
            _exhausted = true;
            if (_cursor is not null)
            {
                await _cursor.CloseAsync(token).ConfigureAwait(false);
            }

            return false;
        }, cancellationToken);

    public override bool Read()
        => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        => RunOperationAsync(ReadCoreAsync, cancellationToken);

    private async Task<bool> ReadCoreAsync(CancellationToken token)
    {
        InvalidateValueAccess();
        _currentRow = null;
        _sequentialOrdinal = -1;
        _sequentialOffset = 0;
        if (_exhausted || ((_behavior & CommandBehavior.SingleRow) != 0 && _rowsRead >= 1))
        {
            _exhausted = true;
            if (_cursor is not null)
            {
                await _cursor.CloseAsync(token).ConfigureAwait(false);
            }

            return false;
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_rowIndex + 1 < _currentRows.Count)
            {
                _rowIndex++;
                _currentRow = _currentRows[_rowIndex];
                _rowsRead++;
                return true;
            }

            if (_cursor is null || _cursor.EndOfData)
            {
                _exhausted = true;
                if (_cursor is not null)
                {
                    await _cursor.CloseAsync(token).ConfigureAwait(false);
                }

                return false;
            }

            var block = await _cursor.FetchAsync(token).ConfigureAwait(false);
            _currentRows = block.Rows;
            _rowIndex = -1;
            ValidateRows(_currentRows);
        }
    }
    public override T GetFieldValue<T>(int ordinal)
    {
        if (typeof(T) == typeof(Stream))
        {
            return (T)(object)GetStream(ordinal);
        }

        if (typeof(T) == typeof(TextReader))
        {
            return (T)(object)GetTextReader(ordinal);
        }

        var value = GetValue(ordinal);
        return ConvertFieldValue<T>(value);
    }

    private static T ConvertFieldValue<T>(object value)
    {
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

    public override async Task<T> GetFieldValueAsync<T>(
        int ordinal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (typeof(T) == typeof(Stream))
        {
            return (T)(object)GetStream(ordinal);
        }

        if (typeof(T) == typeof(TextReader))
        {
            return (T)(object)GetTextReader(ordinal);
        }

        return ConvertFieldValue<T>(await GetValueAsync(ordinal, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Returns a non-seekable stream, valid until another value stream is opened or the reader advances.</summary>
    public override Stream GetStream(int ordinal)
        => CreateValueAccessor(ordinal, generation =>
            new Db2iValueStream(this, generation, ordinal, CreateByteState(GetRawValue(ordinal))));

    /// <summary>Returns a text reader that preserves trailing spaces and decodes text incrementally.</summary>
    public override TextReader GetTextReader(int ordinal)
        => CreateValueAccessor(ordinal, generation =>
            new Db2iValueTextReader(this, generation, ordinal, CreateTextState(GetRawValue(ordinal))));

    internal Task<object> GetValueAsync(int ordinal, CancellationToken cancellationToken)
        => RunOperationAsync(async token =>
        {
            var value = GetRawValue(ordinal);
            EnsureValueAccess(ordinal, 0);
            InvalidateValueAccess();
            if (value is not Db2iLobValue lob)
            {
                if (_columns[ordinal].IsLong)
                {
                    CheckMaterializationSize(value switch
                    {
                        byte[] bytes => bytes.LongLength,
                        string text => 2L * text.Length,
                        _ => 0,
                    });
                }

                SetSequentialOffset(value switch
                {
                    byte[] bytes => bytes.LongLength,
                    string text => text.Length,
                    _ => 0,
                });
                return value;
            }

            if (IsBinaryLob(lob))
            {
                var source = CreateByteState(lob);
                var length = await source.GetLengthAsync(token).ConfigureAwait(false);
                CheckMaterializationSize(length);
                var result = new byte[checked((int)length)];
                var offset = 0;
                while (offset < result.Length)
                {
                    var read = await source.ReadAsync(result.AsMemory(offset), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new Db2iException("IBM i ha restituito un LOB troncato.", Db2iErrorKind.Protocol);
                    }

                    offset += read;
                }

                SetSequentialOffset(length);
                return result;
            }

            var textSource = CreateTextState(lob);
            var characters = new char[LobChunkSize / sizeof(char)];
            var builder = new StringBuilder();
            int count;
            while ((count = await textSource.ReadAsync(characters, token).ConfigureAwait(false)) != 0)
            {
                CheckMaterializationSize(2L * (builder.Length + (long)count));
                builder.Append(characters, 0, count);
            }

            SetSequentialOffset(builder.Length);
            return builder.ToString();
        }, cancellationToken);

    internal Task<long> GetBytesAsync(
        int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length,
        CancellationToken cancellationToken)
        => RunOperationAsync(async token =>
        {
            ValidateCopyArguments(dataOffset, buffer, bufferOffset, length);
            var value = GetRawValue(ordinal);
            EnsureValueAccess(ordinal, dataOffset);
            _valueGeneration++;
            if (buffer is null)
            {
                var size = await CreateByteState(value).GetLengthAsync(token).ConfigureAwait(false);
                return Math.Max(0, size - dataOffset);
            }

            if (_partialOrdinal != ordinal || _partialBytes is null || dataOffset < _partialBytes.Position)
            {
                _partialBytes = CreateByteState(value);
                _partialChars = null;
                _partialOrdinal = ordinal;
            }

            await _partialBytes.SkipToAsync(dataOffset, token).ConfigureAwait(false);
            var copied = 0;
            while (copied < length)
            {
                var read = await _partialBytes.ReadAsync(
                    buffer.AsMemory(bufferOffset + copied, length - copied), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                copied += read;
            }

            SetSequentialOffset(_partialBytes.Position);
            return (long)copied;
        }, cancellationToken);

    internal Task<long> GetCharsAsync(
        int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length,
        CancellationToken cancellationToken)
        => RunOperationAsync(async token =>
        {
            ValidateCopyArguments(dataOffset, buffer, bufferOffset, length);
            var value = GetRawValue(ordinal);
            EnsureValueAccess(ordinal, dataOffset);
            _valueGeneration++;
            if (buffer is null)
            {
                // A length query must not consume the caller's sequential position.
                var counter = CreateTextState(value);
                await counter.SkipToAsync(dataOffset, token).ConfigureAwait(false);
                var discard = new char[LobChunkSize / sizeof(char)];
                long count = 0;
                int read;
                while ((read = await counter.ReadAsync(discard, token).ConfigureAwait(false)) != 0)
                {
                    count = checked(count + read);
                }

                return count;
            }

            if (_partialOrdinal != ordinal || _partialChars is null || dataOffset < _partialChars.Position)
            {
                _partialChars = CreateTextState(value);
                _partialBytes = null;
                _partialOrdinal = ordinal;
            }

            await _partialChars.SkipToAsync(dataOffset, token).ConfigureAwait(false);
            var copied = 0;
            while (copied < length)
            {
                var read = await _partialChars.ReadAsync(
                    buffer.AsMemory(bufferOffset + copied, length - copied), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                copied += read;
            }

            SetSequentialOffset(_partialChars.Position);
            return (long)copied;
        }, cancellationToken);

    internal Task<int> ReadValueStreamAsync(
        int generation, int ordinal, Db2iLobReadState source, Memory<byte> buffer,
        CancellationToken cancellationToken)
        => RunOperationAsync(async token =>
        {
            EnsureValueStream(generation);
            EnsureValueAccess(ordinal, source.Position);
            var count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            SetSequentialOffset(source.Position);
            return count;
        }, cancellationToken);

    internal Task<int> ReadValueTextAsync(
        int generation, int ordinal, Db2iLobTextState source, Memory<char> buffer,
        CancellationToken cancellationToken)
        => RunOperationAsync(async token =>
        {
            EnsureValueStream(generation);
            EnsureValueAccess(ordinal, source.Position);
            var count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            SetSequentialOffset(source.Position);
            return count;
        }, cancellationToken);

    internal ValueTask<Db2iLobChunk> ReadLobCoreAsync(
        Db2iLobValue value, long offset, int length, CancellationToken token)
    {
        EnsureSession();
        var cursor = _cursor ?? throw new InvalidOperationException("Il locator non appartiene a un cursore attivo.");
        return cursor.Session.ReadLobAsync(cursor.Statement.Handle, value, offset, length, token);
    }

    private T CreateValueAccessor<T>(int ordinal, Func<int, T> create)
    {
        EnterOperation();
        try
        {
            EnsureOnRow();
            EnsureOrdinal(ordinal);
            EnsureSession();
            EnsureValueAccess(ordinal, 0);
            InvalidateValueAccess();
            return create(_valueGeneration);
        }
        finally
        {
            ExitOperation();
        }
    }

    private Db2iLobReadState CreateByteState(object value)
        => value switch
        {
            byte[] bytes => new Db2iLobReadState(bytes),
            Db2iLobValue lob when IsBinaryLob(lob) => new Db2iLobReadState(this, lob),
            DBNull => throw new InvalidCastException("La colonna contiene NULL."),
            _ => throw new InvalidCastException("La colonna non contiene dati binari."),
        };

    private Db2iLobTextState CreateTextState(object value)
        => value switch
        {
            string text => new Db2iLobTextState(text),
            Db2iLobValue lob when !IsBinaryLob(lob) => new Db2iLobTextState(
                new Db2iLobReadState(this, lob), IbmIEncoding.Get(lob.Field.Ccsid)),
            DBNull => throw new InvalidCastException("La colonna contiene NULL."),
            _ => throw new InvalidCastException("La colonna non contiene testo."),
        };

    private static bool IsBinaryLob(Db2iLobValue value) => value.Field.NativeType is 404 or 960;

    private static void CheckMaterializationSize(long bytes)
    {
        if (bytes > LobMaterializationLimit)
        {
            throw new InvalidOperationException(
                "Il LOB supera il limite di materializzazione di 64 MiB. Usare GetStream o GetTextReader.");
        }
    }

    private object GetRawValue(int ordinal)
    {
        EnsureOnRow();
        EnsureOrdinal(ordinal);
        EnsureSession();
        return _currentRow![ordinal] ?? DBNull.Value;
    }

    private void EnsureValueAccess(int ordinal, long offset)
    {
        if ((_behavior & CommandBehavior.SequentialAccess) == 0)
        {
            return;
        }

        if (ordinal < _sequentialOrdinal || (ordinal == _sequentialOrdinal && offset < _sequentialOffset))
        {
            throw new InvalidOperationException("SequentialAccess non consente di rileggere colonne o dati precedenti.");
        }

        if (ordinal > _sequentialOrdinal)
        {
            _sequentialOrdinal = ordinal;
            _sequentialOffset = 0;
        }
    }

    private void SetSequentialOffset(long offset) => _sequentialOffset = offset;

    private void InvalidateValueAccess()
    {
        _valueGeneration++;
        _partialOrdinal = -1;
        _partialBytes = null;
        _partialChars = null;
    }

    private void EnsureValueStream(int generation)
    {
        EnsureOnRow();
        if (generation != _valueGeneration)
        {
            throw new InvalidOperationException("Lo stream non è più valido: il reader è avanzato o è stato aperto un altro valore.");
        }
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
        schema.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));

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
                column.AllowNull,
                column.IsLong);
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
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    internal Task CloseWithCancellationAsync(CancellationToken cancellationToken)
        => _closed ? Task.CompletedTask : RunOperationAsync(async token =>
        {
            await CloseCoreAsync(skipCursorClose: false, token).ConfigureAwait(false);
            return true;
        }, cancellationToken, closing: true);

    private async ValueTask CloseCoreAsync(bool skipCursorClose, CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        InvalidateValueAccess();
        _currentRow = null;
        _currentRows = [];
        Exception? closeException = null;
        var leaseHeld = _lease is not null;
        using var cleanupTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(_commandTimeout > 0 ? _commandTimeout : 15));
        using var cleanupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, cleanupTimeout.Token);
        try
        {
            if (!skipCursorClose && _cursor is not null && IsOriginalSession())
            {
                await _cursor.CloseAsync(cleanupCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            closeException = exception;
            await AbortOriginalConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_command is not null)
            {
                try
                {
                    await _command.ReaderClosedAsync(this, cleanupCancellation.Token, leaseHeld)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    closeException ??= exception;
                    await AbortOriginalConnectionAsync().ConfigureAwait(false);
                }
            }

            _lease?.Dispose();
            _lease = null;

            if (!skipCursorClose
                && (_behavior & CommandBehavior.CloseConnection) != 0
                && _connection is not null && IsOriginalSession())
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
        }

        if (closeException is OperationCanceledException && cleanupTimeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new Db2iException("Timeout durante la chiusura del reader e il rilascio dei locator LOB.",
                Db2iErrorKind.Timeout, closeException, isTransient: true);
        }

        if (closeException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeException).Throw();
        }
    }

    private async Task<T> RunOperationAsync<T>(
        Func<CancellationToken, Task<T>> action, CancellationToken callerToken, bool closing = false)
    {
        callerToken.ThrowIfCancellationRequested();
        EnterOperation();
        try
        {
            EnsureNotClosed();
            if (!closing)
            {
                EnsureSession();
            }

            using var operation = CreateReaderOperationCancellation(callerToken, closing);
            try
            {
                return await action(operation?.Token ?? callerToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                var synchronized = IsOriginalSession() && _connection is not null
                    && await _connection.CancelPendingOperationAsync().ConfigureAwait(false);
                try
                {
                    await CloseCoreAsync(skipCursorClose: !synchronized).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve cancellation; CloseCore already discards uncertain sessions.
                }

                if (callerToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("La lettura SQL è stata annullata dal chiamante.",
                        exception, callerToken);
                }

                if (operation?.TimedOut == true)
                {
                    throw new Db2iException("Timeout durante la lettura SQL o LOB su IBM i.",
                        Db2iErrorKind.Timeout, exception, isTransient: true);
                }

                throw;
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException
                || exception is Db2iException { Kind: Db2iErrorKind.Protocol })
            {
                await AbortOriginalConnectionAsync().ConfigureAwait(false);
                try
                {
                    await CloseCoreAsync(skipCursorClose: true).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original communication or protocol failure.
                }

                if (exception is Db2iException)
                {
                    throw;
                }

                throw new Db2iException("La connessione IBM i si è interrotta durante la lettura SQL o LOB.",
                    Db2iErrorKind.Connection, exception, isTransient: true);
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    private Db2iCommand.OperationCancellation? CreateReaderOperationCancellation(
        CancellationToken callerToken, bool closing)
    {
        try
        {
            return _command?.CreateOperationCancellation(callerToken);
        }
        catch (ObjectDisposedException) when (closing)
        {
            // A disposed command still owns its active reader. Its reader must
            // release the cursor and statement with CloseCore's bounded token.
            return null;
        }
    }

    private bool IsOriginalSession()
    {
        if (_connection is null)
        {
            return false;
        }

        try
        {
            return ReferenceEquals(_connection.GetOpenSession(), _session);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void EnsureSession()
    {
        if (_session is not null && !IsOriginalSession())
        {
            throw new InvalidOperationException("La sessione IBM i del data reader non è più attiva.");
        }
    }

    private Task AbortOriginalConnectionAsync()
        => IsOriginalSession() ? _connection!.AbortCommandAsync() : Task.CompletedTask;

    private void EnterOperation()
    {
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("Il data reader ha già un'operazione in corso.");
        }
    }

    private void ExitOperation() => Volatile.Write(ref _operationActive, 0);

    private static void ValidateCopyArguments<T>(long dataOffset, T[]? buffer, int bufferOffset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (buffer is not null && (bufferOffset > buffer.Length || length > buffer.Length - bufferOffset))
        {
            throw new ArgumentException("Il buffer di destinazione non è abbastanza grande.", nameof(buffer));
        }
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
    int NumericScale = 0,
    bool IsLong = false);
