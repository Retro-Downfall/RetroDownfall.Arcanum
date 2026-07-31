using System.Net;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class BoundedHttpContentReaderTests
{
    [Fact]
    public async Task TryReadAsync_rejects_declared_oversize_content_without_opening_stream()
    {
        using ThrowingContent content = new();
        content.Headers.ContentLength = 33;

        byte[]? result = await BoundedHttpContentReader.TryReadAsync(
            content,
            maxBytes: 32,
            CancellationToken.None);

        Assert.Null(result);
        Assert.False(content.WasRead);
    }

    [Fact]
    public async Task TryReadAsync_stops_after_limit_when_length_is_unknown()
    {
        byte[] payload = Enumerable.Range(0, 1_000).Select(static i => (byte)i).ToArray();
        using MemoryStream stream = new(payload);
        using StreamContent content = new(stream);

        byte[]? result = await BoundedHttpContentReader.TryReadAsync(
            content,
            maxBytes: 32,
            CancellationToken.None);

        Assert.Null(result);
        Assert.InRange(stream.Position, 0, 33);
    }

    [Fact]
    public async Task TryReadAsync_returns_content_within_limit()
    {
        byte[] payload = [1, 2, 3, 4];
        using ByteArrayContent content = new(payload);

        byte[]? result = await BoundedHttpContentReader.TryReadAsync(
            content,
            maxBytes: 32,
            CancellationToken.None);

        Assert.Equal(payload, result);
    }

    private sealed class ThrowingContent : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            WasRead = true;

            throw new InvalidOperationException("Oversize content must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}
