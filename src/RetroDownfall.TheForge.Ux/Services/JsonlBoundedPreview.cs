namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Bounded JSONL preview: reads at most <see cref="DefaultMaxLines"/> lines and
/// <see cref="DefaultMaxBytes"/> bytes from a stream without loading the whole file into memory.
/// </summary>
public static class JsonlBoundedPreview
{

    public const int DefaultMaxLines = 50;

    public const int DefaultMaxBytes = 256 * 1024;

    /// <summary>
    /// Reads lines from <paramref name="stream"/> until line or byte bounds are hit.
    /// Does not rewind or dispose the stream.
    /// </summary>
    public static async Task<JsonlPreviewResult> ReadAsync(
        Stream stream,
        int maxLines = DefaultMaxLines,
        int maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {

        if (maxLines < 1)
        {

            maxLines = 1;

        }

        if (maxBytes < 1)
        {

            maxBytes = 1;

        }

        using MemoryStream boundedBytes = new(Math.Min(maxBytes, 81_920));

        byte[] readBuffer = new byte[Math.Min(maxBytes, 81_920)];

        while (boundedBytes.Length < maxBytes)
        {

            cancellationToken.ThrowIfCancellationRequested();

            int remaining = maxBytes - checked((int)boundedBytes.Length);

            int read = await stream
                .ReadAsync(readBuffer.AsMemory(0, Math.Min(readBuffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {

                break;

            }

            await boundedBytes
                .WriteAsync(readBuffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

        }

        bool truncated = false;

        if (boundedBytes.Length == maxBytes)
        {

            byte[] probe = new byte[1];

            truncated = await stream
                .ReadAsync(probe, cancellationToken)
                .ConfigureAwait(false) != 0;

        }

        string text = System.Text.Encoding.UTF8.GetString(boundedBytes.GetBuffer(), 0, checked((int)boundedBytes.Length));

        List<string> lines = new(Math.Min(maxLines, 64));

        using StringReader reader = new(text);

        while (lines.Count < maxLines && reader.ReadLine() is { } line)
        {

            lines.Add(line);

        }

        if (lines.Count >= maxLines && reader.ReadLine() is not null)
        {

            truncated = true;

        }

        return new JsonlPreviewResult(lines, truncated, checked((int)boundedBytes.Length));

    }

}

/// <summary>Result of a bounded JSONL preview read.</summary>
public sealed record JsonlPreviewResult(
    IReadOnlyList<string> Lines,
    bool Truncated,
    int BytesRead);
