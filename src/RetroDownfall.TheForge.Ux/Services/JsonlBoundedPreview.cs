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

        List<string> lines = new(Math.Min(maxLines, 64));

        using StreamReader reader = new(stream, leaveOpen: true);

        int bytesRead = 0;

        bool truncated = false;

        while (lines.Count < maxLines)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {

                break;

            }

            // Count UTF-8 bytes of the line content + a newline separator estimate.
            int lineBytes = System.Text.Encoding.UTF8.GetByteCount(line) + 1;

            if (bytesRead + lineBytes > maxBytes && lines.Count > 0)
            {

                truncated = true;

                break;

            }

            bytesRead += lineBytes;

            lines.Add(line);

            if (bytesRead >= maxBytes)
            {

                truncated = true;

                break;

            }

        }

        if (!truncated && lines.Count >= maxLines)
        {

            // Peek one more line to know whether more content remains.
            string? peek = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (peek is not null)
            {

                truncated = true;

            }

        }

        return new JsonlPreviewResult(lines, truncated, bytesRead);

    }

}

/// <summary>Result of a bounded JSONL preview read.</summary>
public sealed record JsonlPreviewResult(
    IReadOnlyList<string> Lines,
    bool Truncated,
    int BytesRead);
