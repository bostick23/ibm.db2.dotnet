namespace Db2i;

// Returned streams keep the batch's timeout, cancellation and error attribution
// active for each read, including reads performed after ExecuteReader returns.
internal sealed class Db2iBatchLobStream(
    Stream inner,
    Func<Func<CancellationToken, Task<int>>, CancellationToken, Task<int>> execute) : Stream
{
    private bool _disposed;
    public override bool CanRead => !_disposed && inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(execute(token => inner.ReadAsync(buffer, token).AsTask(), cancellationToken));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; inner.Dispose(); }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed) { _disposed = true; await inner.DisposeAsync().ConfigureAwait(false); }
        GC.SuppressFinalize(this);
    }
}

internal sealed class Db2iBatchLobTextReader(
    TextReader inner,
    Func<Func<CancellationToken, Task<int>>, CancellationToken, Task<int>> execute) : TextReader
{
    private bool _disposed;
    private int _peeked = -1;
    public override int Peek()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return execute(async token =>
        {
            if (_peeked < 0)
            {
                var character = new char[1];
                if (await inner.ReadAsync(character.AsMemory(), token).ConfigureAwait(false) > 0)
                { _peeked = character[0]; }
            }
            else
            {
                await inner.ReadAsync(Memory<char>.Empty, token).ConfigureAwait(false);
            }
            return _peeked;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
    public override int Read()
    {
        var character = new char[1];
        return Read(character, 0, 1) == 0 ? -1 : character[0];
    }
    public override int Read(char[] buffer, int index, int count)
        => ReadAsync(buffer.AsMemory(index, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(char[] buffer, int index, int count)
        => ReadAsync(buffer.AsMemory(index, count)).AsTask();
    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(execute(async token =>
        {
            if (buffer.IsEmpty || _peeked >= 0)
            {
                await inner.ReadAsync(Memory<char>.Empty, token).ConfigureAwait(false);
            }
            if (buffer.IsEmpty) { return 0; }
            if (_peeked >= 0)
            {
                buffer.Span[0] = (char)_peeked;
                _peeked = -1;
                return 1;
            }
            return await inner.ReadAsync(buffer, token).ConfigureAwait(false);
        }, cancellationToken));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; inner.Dispose(); }
        base.Dispose(disposing);
    }
}
