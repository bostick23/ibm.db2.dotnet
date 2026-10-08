using System.Buffers;

namespace Db2i;

// The reader owns locator lifetime and operation cancellation. Disposing an
// accessor releases only the accessor; row/block advancement releases locators.
internal sealed class Db2iValueStream(
    Db2iDataReader owner, int generation, int ordinal, Db2iLobReadState source) : Stream
{
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        var temporary = ArrayPool<byte>.Shared.Rent(Math.Max(1, Math.Min(buffer.Length, Db2iDataReader.LobChunkSize)));
        try
        {
            var count = Read(temporary, 0, Math.Min(buffer.Length, temporary.Length));
            temporary.AsSpan(0, count).CopyTo(buffer);
            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temporary);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(owner.ReadValueStreamAsync(generation, ordinal, source, buffer, cancellationToken));
    }

    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class Db2iValueTextReader(
    Db2iDataReader owner, int generation, int ordinal, Db2iLobTextState source) : TextReader
{
    private bool _disposed;
    private int _peeked = -1;

    public override int Peek()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_peeked >= 0)
        {
            // Even a buffered character must become inaccessible after advance.
            owner.ReadValueTextAsync(generation, ordinal, source, Memory<char>.Empty,
                CancellationToken.None).GetAwaiter().GetResult();
            return _peeked;
        }

        var character = new char[1];
        if (owner.ReadValueTextAsync(generation, ordinal, source, character,
            CancellationToken.None).GetAwaiter().GetResult() != 0)
        {
            _peeked = character[0];
        }

        return _peeked;
    }

    public override int Read()
    {
        var character = new char[1];
        return Read(character, 0, 1) == 0 ? -1 : character[0];
    }

    public override int Read(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(index, count)).AsTask().GetAwaiter().GetResult();
    }

    public override int Read(Span<char> buffer)
    {
        var temporary = ArrayPool<char>.Shared.Rent(Math.Max(1,
            Math.Min(buffer.Length, Db2iDataReader.LobChunkSize / sizeof(char))));
        try
        {
            var count = Read(temporary, 0, Math.Min(buffer.Length, temporary.Length));
            temporary.AsSpan(0, count).CopyTo(buffer);
            return count;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(temporary);
        }
    }

    public override Task<int> ReadAsync(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(index, count)).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_peeked >= 0 && !buffer.IsEmpty)
        {
            await owner.ReadValueTextAsync(generation, ordinal, source, Memory<char>.Empty,
                cancellationToken).ConfigureAwait(false);
            buffer.Span[0] = (char)_peeked;
            _peeked = -1;
            return 1;
        }

        return await owner.ReadValueTextAsync(generation, ordinal, source, buffer,
            cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _disposed = true;
        base.Dispose(disposing);
    }
}
