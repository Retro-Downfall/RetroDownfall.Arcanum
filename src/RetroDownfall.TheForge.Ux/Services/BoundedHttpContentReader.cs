namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Buffers an HTTP body up to a fixed ceiling. The Forge normally talks to a loopback Arcanum
/// process, but the endpoint is configurable and must not be able to force unbounded allocations.
/// </summary>
internal static class BoundedHttpContentReader
{
    internal const long DefaultMaxBytes = 64L * 1024L * 1024L;

    public static async Task<byte[]?> TryReadAsync(
        HttpContent content,
        long maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        if (content.Headers.ContentLength is { } declaredLength && declaredLength > maxBytes)
        {
            return null;
        }

        Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using MemoryStream buffer = new(capacity: checked((int)Math.Min(maxBytes, 81_920L)));

        byte[] chunk = new byte[81_920];

        long total = 0;

        while (true)
        {
            int readSize = checked((int)Math.Min(chunk.Length, (maxBytes - total) + 1));

            int read = await stream
                .ReadAsync(chunk.AsMemory(0, readSize), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return buffer.ToArray();
            }

            total += read;

            if (total > maxBytes)
            {
                return null;
            }

            await buffer
                .WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
