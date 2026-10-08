using System.Runtime.Versioning;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A response body that looks at the download staging file the first time it is read, which is after
/// the file was created and before anything was written into it, and records its Unix mode.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ModePeekingStream(string directory, byte[] payload) : Stream
{
    private readonly MemoryStream _inner = new(payload);

    public bool ObservedStagingFile { get; private set; }

    public UnixFileMode StagingMode { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        Observe();

        return _inner.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        Observe();

        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Observe()
    {
        if (ObservedStagingFile)
        {
            return;
        }

        string[] staging = Directory.GetFiles(directory, "*.download");

        if (staging.Length == 1)
        {
            ObservedStagingFile = true;

            StagingMode = File.GetUnixFileMode(staging[0]);
        }
    }
}
