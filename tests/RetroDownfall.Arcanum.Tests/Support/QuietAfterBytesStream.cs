namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Serves the bytes it was given, as many per read as the consumer asks for, and then goes quiet the
/// way an open socket does when the server has sent everything it has for now: the next read waits
/// until the consumer cancels or disposes the stream instead of reporting the end of it. A consumer
/// that goes back to the stream for more than it needs to hand over what it already has therefore
/// stalls, which an in-memory body that ends cleanly can never show.
/// </summary>
public sealed class QuietAfterBytesStream(byte[] content) : Stream
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _position;

    private int _readsServed;

    /// <summary>
    /// How many reads found bytes to serve.
    /// </summary>
    public int ReadsServed => Volatile.Read(ref _readsServed);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();

        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_position < content.Length)
        {
            int count = Math.Min(buffer.Length, content.Length - _position);

            content.AsMemory(_position, count).CopyTo(buffer);

            _position += count;

            _ = Interlocked.Increment(ref _readsServed);

            return count;
        }

        await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        return 0;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ = _closed.TrySetResult();
        }

        base.Dispose(disposing);
    }
}
