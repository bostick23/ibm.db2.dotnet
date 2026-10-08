using System.Text;
using Db2i.Protocol;

namespace Db2i;

// These states are used only inside a reader operation. They deliberately do not
// register command cancellation: the caller owns that registration for the whole read.
internal sealed class Db2iLobReadState
{
    private readonly Db2iDataReader? _owner;
    private readonly Db2iLobValue? _value;
    private readonly byte[]? _inline;
    private ReadOnlyMemory<byte> _chunk;
    private int _chunkOffset;
    private long _nativeOffset;
    private long? _nativeLength;

    internal Db2iLobReadState(byte[] inline) => _inline = inline;

    internal Db2iLobReadState(Db2iDataReader owner, Db2iLobValue value)
    {
        _owner = owner;
        _value = value;
    }

    internal long Position { get; private set; }

    private int UnitSize => _value?.Field.IsGraphicLob == true ? 2 : 1;

    internal async ValueTask<long> GetLengthAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_inline is not null)
        {
            return _inline.LongLength;
        }

        if (_nativeLength is null)
        {
            var result = await _owner!.ReadLobCoreAsync(_value!, 0, 0, token).ConfigureAwait(false);
            ValidateLength(result.TotalLength);
            _nativeLength = result.TotalLength;
        }

        return checked(_nativeLength.Value * UnitSize);
    }

    internal async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (destination.IsEmpty)
        {
            return 0;
        }

        if (_inline is not null)
        {
            var count = (int)Math.Min(destination.Length, _inline.LongLength - Position);
            _inline.AsMemory(checked((int)Position), count).CopyTo(destination);
            Position += count;
            return count;
        }

        if (_chunkOffset >= _chunk.Length)
        {
            if (_nativeLength is { } knownLength && _nativeOffset >= knownLength)
            {
                return 0;
            }

            var amount = (int)Math.Min(Db2iDataReader.LobChunkSize / UnitSize,
                int.MaxValue - _nativeOffset);
            if (_nativeLength is { } totalLength)
            {
                amount = (int)Math.Min(amount, totalLength - _nativeOffset);
            }

            var result = await _owner!.ReadLobCoreAsync(_value!, _nativeOffset,
                amount, token).ConfigureAwait(false);
            ValidateLength(result.TotalLength);
            if (_nativeLength is { } previousLength && previousLength != result.TotalLength)
            {
                throw ProtocolError("La lunghezza del locator LOB è cambiata durante la lettura.");
            }

            _nativeLength = result.TotalLength;
            if (result.Data.Length > Db2iDataReader.LobChunkSize || result.Data.Length % UnitSize != 0)
            {
                throw ProtocolError("La lunghezza del blocco LOB IBM i non è valida.");
            }

            var units = result.Data.Length / UnitSize;
            if (units > result.TotalLength - _nativeOffset
                || (units == 0 && _nativeOffset < result.TotalLength))
            {
                throw ProtocolError("IBM i ha restituito un blocco LOB troncato o incoerente.");
            }

            _nativeOffset = checked(_nativeOffset + units);
            _chunk = result.Data;
            _chunkOffset = 0;
            if (_chunk.IsEmpty)
            {
                return 0;
            }
        }

        var copied = Math.Min(destination.Length, _chunk.Length - _chunkOffset);
        _chunk.Slice(_chunkOffset, copied).CopyTo(destination);
        _chunkOffset += copied;
        Position = checked(Position + copied);
        return copied;
    }

    internal async ValueTask SkipToAsync(long offset, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (offset <= Position)
        {
            return;
        }

        if (_inline is not null)
        {
            Position = Math.Min(offset, _inline.LongLength);
            return;
        }

        // Binary offsets are byte offsets. GetBytes never uses this state for
        // character locators; text skipping must run its incremental decoder.
        var length = await GetLengthAsync(token).ConfigureAwait(false);
        Position = Math.Min(offset, length);
        _nativeOffset = Position / UnitSize;
        _chunk = default;
        _chunkOffset = 0;
    }

    private static void ValidateLength(long length)
    {
        if (length < 0 || length > int.MaxValue)
        {
            throw ProtocolError("La lunghezza LOB supera l'intervallo di offset supportato dal protocollo IBM i.");
        }
    }

    private static Db2iException ProtocolError(string message) => new(message, Db2iErrorKind.Protocol);
}

internal sealed class Db2iLobTextState
{
    private readonly string? _inline;
    private readonly Db2iLobReadState? _source;
    private readonly Decoder? _decoder;
    private readonly byte[]? _bytes;
    private readonly char[]? _chars;
    private int _charOffset;
    private int _charCount;
    private bool _complete;

    internal Db2iLobTextState(string text) => _inline = text;

    internal Db2iLobTextState(Db2iLobReadState source, Encoding encoding)
    {
        _source = source;
        _decoder = encoding.GetDecoder();
        _bytes = new byte[Db2iDataReader.LobChunkSize];
        _chars = new char[encoding.GetMaxCharCount(Db2iDataReader.LobChunkSize)];
    }

    internal long Position { get; private set; }

    internal async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (destination.IsEmpty)
        {
            return 0;
        }

        if (_inline is not null)
        {
            var count = (int)Math.Min(destination.Length, _inline.Length - Position);
            _inline.AsMemory(checked((int)Position), count).CopyTo(destination);
            Position += count;
            return count;
        }

        while (_charOffset == _charCount)
        {
            if (_complete)
            {
                return 0;
            }

            var count = await _source!.ReadAsync(_bytes!, token).ConfigureAwait(false);
            _complete = count == 0;
            try
            {
                _charCount = _decoder!.GetChars(_bytes!, 0, count, _chars!, 0, flush: _complete);
            }
            catch (DecoderFallbackException exception)
            {
                throw new Db2iException("IBM i ha restituito una sequenza di testo LOB non valida o troncata.",
                    Db2iErrorKind.Protocol, exception);
            }
            _charOffset = 0;
            // A multibyte sequence or a surrogate pair may straddle two blocks.
        }

        var copied = Math.Min(destination.Length, _charCount - _charOffset);
        _chars!.AsMemory(_charOffset, copied).CopyTo(destination);
        _charOffset += copied;
        Position = checked(Position + copied);
        return copied;
    }

    internal async ValueTask SkipToAsync(long offset, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_inline is not null)
        {
            Position = Math.Min(offset, _inline.Length);
            return;
        }

        var discard = new char[Db2iDataReader.LobChunkSize / sizeof(char)];
        while (Position < offset)
        {
            var count = (int)Math.Min(discard.Length, offset - Position);
            if (await ReadAsync(discard.AsMemory(0, count), token).ConfigureAwait(false) == 0)
            {
                return;
            }
        }
    }
}
