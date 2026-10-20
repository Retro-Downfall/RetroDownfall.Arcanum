using System.Net;
using System.Text;
using System.Text.Json;

namespace RetroDownfall.Arcanum.PromptCache.Benchmarks;

internal sealed class BenchmarkObservedContent : HttpContent
{
    private readonly HttpContent _inner;

    private readonly Action<double?, double?> _timings;

    internal BenchmarkObservedContent(HttpContent inner, Action<double?, double?> timings)
    {
        _inner = inner;
        _timings = timings;

        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override bool TryComputeLength(out long length) { length = 0; return false; }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await _inner.CopyToAsync(stream);

    protected override async Task<Stream> CreateContentReadStreamAsync() => new BenchmarkObservedStream(await _inner.ReadAsStreamAsync(), _timings);

    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}

internal sealed class BenchmarkObservedStream(Stream inner, Action<double?, double?> timings) : Stream
{
    private readonly MemoryStream _line = new();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);

        Observe(buffer.AsSpan(offset, read));

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);

        Observe(buffer.Span[..read]);

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Observe(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value == (byte)'\n')
            {
                string line = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length);

                _line.SetLength(0);

                if (line.StartsWith("data: ", StringComparison.Ordinal) && line != "data: [DONE]")
                {
                    using JsonDocument parsed = JsonDocument.Parse(line.AsSpan(6).ToString());

                    var root = parsed.RootElement;

                    if (root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True) throw new InvalidDataException("contextTruncationObserved");

                    double? prompt = null;

                    double? load = null;

                    if (root.TryGetProperty("timings", out var t) && t.TryGetProperty("prompt_ms", out var p) && p.TryGetDouble(out double pm) && double.IsFinite(pm) && pm >= 0) prompt = pm;

                    if (root.TryGetProperty("load_duration", out var l) && l.TryGetDouble(out double lm) && double.IsFinite(lm) && lm >= 0) load = lm / 1_000_000;

                    timings(prompt, load);
                }
            }
            else
            {
                if (_line.Length >= 64 * 1024) throw new InvalidDataException("providerFrameBoundExceeded");

                _line.WriteByte(value);
            }
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) { _line.Dispose(); inner.Dispose(); } base.Dispose(disposing); }
}
