namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A response body whose first read waits for the test to release it, so a test decides exactly when
/// the body starts to arrive instead of racing a wall-clock delay. <see cref="ReadStarted"/> completes
/// once the consumer is blocked on the body, which is the moment the response headers are known to
/// have been delivered.
/// </summary>
public sealed class GatedBodyStream(byte[] payload) : Stream
{
    private readonly MemoryStream _inner = new(payload);

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ReadStarted => _started.Task;

    public void Release() => _gate.TrySetResult();

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
        _ = _started.TrySetResult();

        await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
}
