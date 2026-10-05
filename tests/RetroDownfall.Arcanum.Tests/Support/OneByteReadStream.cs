namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Serves the bytes it was given but never more than one per read, on both the sync and the async
/// path, and remembers the largest read it was asked to satisfy so a test can prove it was in the
/// loop.
/// </summary>
public sealed class OneByteReadStream(byte[] content) : Stream
{
    private int _position;

    public int LargestRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();

        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _position >= content.Length)
        {
            return 0;
        }

        buffer[0] = content[_position++];

        LargestRead = Math.Max(LargestRead, 1);

        return 1;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

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
