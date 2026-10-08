namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// A read-only view of a child's output pipe that hands at most <c>capBytes</c> to its reader and keeps draining the
/// pipe after that, discarding what it reads, so a helper that prints more than the cap still runs to completion
/// instead of blocking on a full pipe, while the caller holds no more than the cap in memory. The reader sees end of
/// stream only when the child closes its end.
/// </summary>
internal sealed class CappedOutputStream(Stream inner, int capBytes) : Stream
{
    private int _delivered;

    /// <summary>
    /// <see langword="true"/> once the child wrote more than the cap and the excess was discarded.
    /// </summary>
    internal bool Truncated { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();

        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return 0;
            }

            int room = capBytes - _delivered;

            if (room > 0)
            {
                int delivered = Math.Min(read, room);

                _delivered += delivered;

                Truncated |= delivered < read;

                return delivered;
            }

            Truncated = true;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            int read = inner.Read(buffer, offset, count);

            if (read == 0)
            {
                return 0;
            }

            int room = capBytes - _delivered;

            if (room > 0)
            {
                int delivered = Math.Min(read, room);

                _delivered += delivered;

                Truncated |= delivered < read;

                return delivered;
            }

            Truncated = true;
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
