using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>The outcome of a capped read: the text, or the fact that the input went over its cap.</summary>
/// <param name="Text">The text read; empty when <paramref name="TooLarge"/>.</param>
/// <param name="TooLarge">Whether the input exceeded the cap, in which case nothing was kept.</param>
internal readonly record struct CappedTextRead(string Text, bool TooLarge);

/// <summary>
/// The one bounded reader for operator-named files and piped text, so that no verb buffers an input of
/// unknown size whole.
/// </summary>
/// <remarks>
/// A path that is not what the operator thought it was (a log, a database, a device that never ends)
/// would otherwise be read to the end before any limit applied. The cap is counted in UTF-8 bytes of what
/// was read, and a regular file that already reports a length over the cap is refused without being read.
/// </remarks>
internal static class CappedInputReader
{
    /// <summary>
    /// The default cap for authored text and import documents: the host's own default request-body limit,
    /// because anything larger would be refused by the host anyway.
    /// </summary>
    internal const int MaxAuthoredBytes = (int)ArcanumRuntimeDefaults.HostMaxRequestBodyBytes;

    private const int ChunkChars = 4096;

    internal static string TooLargeMessage(string source, long maxBytes) =>
        $"{source} is larger than the {maxBytes}-byte input limit.";

    internal static CappedTextRead ReadFile(string path, long maxBytes)
    {
        using StreamReader reader = new(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return ExceedsLength(reader, maxBytes)
            ? new CappedTextRead(string.Empty, TooLarge: true)
            : ReadText(reader, maxBytes);
    }

    internal static async Task<CappedTextRead> ReadFileAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return ExceedsLength(reader, maxBytes)
            ? new CappedTextRead(string.Empty, TooLarge: true)
            : await ReadTextAsync(reader, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    internal static CappedTextRead ReadText(TextReader reader, long maxBytes)
    {
        StringBuilder buffer = new();

        char[] chunk = new char[ChunkChars];

        long byteCount = 0;

        int read;

        while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
        {
            byteCount += Encoding.UTF8.GetByteCount(chunk, 0, read);

            if (byteCount > maxBytes)
            {
                return new CappedTextRead(string.Empty, TooLarge: true);
            }

            buffer.Append(chunk, 0, read);
        }

        return new CappedTextRead(buffer.ToString(), TooLarge: false);
    }

    internal static async Task<CappedTextRead> ReadTextAsync(
        TextReader reader,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        StringBuilder buffer = new();

        char[] chunk = new char[ChunkChars];

        long byteCount = 0;

        int read;

        while ((read = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            byteCount += Encoding.UTF8.GetByteCount(chunk, 0, read);

            if (byteCount > maxBytes)
            {
                return new CappedTextRead(string.Empty, TooLarge: true);
            }

            buffer.Append(chunk, 0, read);
        }

        return new CappedTextRead(buffer.ToString(), TooLarge: false);
    }

    private static bool ExceedsLength(StreamReader reader, long maxBytes) =>
        reader.BaseStream is { CanSeek: true } stream && stream.Length > maxBytes;
}
