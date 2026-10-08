namespace Db2i.Protocol;

// JTOpen SQLBlobLocator/SQLClobLocator upload into descriptor-supplied locator
// handles immediately before execution, truncating previous parameter contents.
internal static class Db2iLobUploader
{
    private const int ChunkSize = 64 * 1024;

    internal static async ValueTask UploadAsync(
        Db2iSession session,
        Db2iPreparedStatement statement,
        IReadOnlyList<Db2iParameter> parameters,
        CancellationToken cancellationToken)
    {
        var fields = statement.ParameterFormat!.Fields;
        try
        {
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                var parameter = parameters[index];
                if (!field.IsLobLocator || parameter.Value is null or DBNull)
                {
                    continue;
                }

                ArgumentOutOfRangeException.ThrowIfNegative(parameter.Size);
                cancellationToken.ThrowIfCancellationRequested();
                if (field.NativeType == 960 || field.Ccsid == 65535)
                {
                    await UploadBinaryAsync(session, statement, field, parameter, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await UploadTextAsync(session, statement, field, parameter, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Partial locator contents must never become a reusable prepared input.
            statement.InvalidateLobPreparation();
            throw;
        }
    }

    private static async ValueTask UploadBinaryAsync(
        Db2iSession session, Db2iPreparedStatement statement, Db2iFieldDescriptor field,
        Db2iParameter parameter, CancellationToken token)
    {
        long offset = 0;
        if (parameter.Value is byte[] bytes)
        {
            CheckSize(bytes.LongLength, parameter.Size);
            CheckServerSize(bytes.LongLength, field);
            do
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(ChunkSize, bytes.LongLength - offset);
                await session.WriteLobAsync(statement, field, offset,
                    bytes.AsMemory((int)offset, count), truncate: true, token).ConfigureAwait(false);
                offset += count;
            } while (offset < bytes.LongLength);
            return;
        }

        if (parameter.Value is not Stream input || !input.CanRead)
        {
            throw new ArgumentException("Il parametro BLOB richiede byte[] o uno Stream leggibile.");
        }

        var buffer = new byte[ChunkSize];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0 && offset > 0) { break; }
            var end = checked(offset + count);
            CheckSize(end, parameter.Size);
            CheckServerSize(end, field);
            await session.WriteLobAsync(statement, field, offset,
                buffer.AsMemory(0, count), truncate: true, token).ConfigureAwait(false);
            offset = end;
            if (count == 0) { break; }
        }
    }

    private static async ValueTask UploadTextAsync(
        Db2iSession session, Db2iPreparedStatement statement, Db2iFieldDescriptor field,
        Db2iParameter parameter, CancellationToken token)
    {
        // StringReader belongs to us; a caller's TextReader always remains open.
        using var stringReader = parameter.Value is string text ? new StringReader(text) : null;
        var input = stringReader ?? parameter.Value as TextReader
            ?? throw new ArgumentException("Il parametro CLOB/DBCLOB richiede string o TextReader.");
        var encoder = IbmIEncoding.Get(field.Ccsid).GetEncoder();
        var characters = new char[16 * 1024];
        var bytes = new byte[ChunkSize];
        long characterCount = 0;
        long nativeOffset = 0;
        var wroteData = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(characters.AsMemory(), token).ConfigureAwait(false);
            characterCount = checked(characterCount + read);
            CheckSize(characterCount, parameter.Size);
            var endOfInput = read == 0;
            var position = 0;
            bool completed;
            do
            {
                encoder.Convert(characters.AsSpan(position, read - position), bytes,
                    flush: endOfInput, out var charsUsed, out var bytesUsed, out completed);
                position += charsUsed;
                if (bytesUsed > 0 || (endOfInput && completed && !wroteData))
                {
                    if (field.IsGraphicLob && (bytesUsed & 1) != 0)
                    {
                        throw new InvalidOperationException("La codifica DBCLOB deve produrre coppie di byte.");
                    }

                    var nativeCount = field.IsGraphicLob ? bytesUsed / 2 : bytesUsed;
                    var end = checked(nativeOffset + nativeCount);
                    CheckServerSize(end, field);
                    await session.WriteLobAsync(statement, field, nativeOffset,
                        bytes.AsMemory(0, bytesUsed), truncate: true, token).ConfigureAwait(false);
                    nativeOffset = end;
                    wroteData = true;
                }
            } while (!completed);

            if (endOfInput) { break; }
        }
    }

    private static void CheckSize(long actual, int requestedMaximum)
    {
        if (requestedMaximum > 0 && actual > requestedMaximum)
        {
            throw new ArgumentException("Il valore LOB supera la Size del parametro; il valore non è stato troncato.");
        }
    }

    private static void CheckServerSize(long nativeLength, Db2iFieldDescriptor field)
    {
        if (nativeLength > int.MaxValue || (field.LobMaxSize > 0 && nativeLength > field.LobMaxSize))
        {
            throw new ArgumentException("Il valore LOB supera la dimensione supportata dal descrittore IBM i.");
        }
    }
}
