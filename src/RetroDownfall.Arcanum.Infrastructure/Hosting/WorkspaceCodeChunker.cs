namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Deterministic, parser-free workspace chunker. It preserves source text and line ranges, prefers
/// headings and top-level declaration-shaped lines as stable boundaries, and never splits a UTF-16
/// surrogate pair. The implementation intentionally avoids reflection-heavy language parsers so the
/// Native AOT surface remains unchanged.
/// </summary>
internal static class WorkspaceCodeChunker
{

    private static readonly string[] DeclarationPrefixes =
    [
        "class ",
        "interface ",
        "record ",
        "struct ",
        "enum ",
        "namespace ",
        "public class ",
        "public interface ",
        "public record ",
        "public struct ",
        "public enum ",
        "internal class ",
        "internal sealed class ",
        "internal static class ",
        "sealed class ",
        "static class ",
        "def ",
        "async def ",
        "function ",
        "export function ",
        "func ",
        "fn ",
        "type ",
        "trait ",
        "impl ",
    ];

    internal sealed record Chunk(string Content, int CharOffset, int StartLine, int EndLine);

    public static Chunk[] ChunkText(string content, string extension, int maxChunkChars)
    {

        if (string.IsNullOrEmpty(content))
        {

            return [];

        }

        List<LineSlice> lines = ReadLines(content);

        List<Chunk> chunks = [];

        int chunkStartLineIndex = 0;

        int chunkChars = 0;

        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {

            LineSlice line = lines[lineIndex];

            bool stableBoundary =
                lineIndex > chunkStartLineIndex
                && IsStableBoundary(content.AsSpan(line.Offset, line.ContentLength), extension);

            if (stableBoundary && chunkChars > 0)
            {

                AddRange(content, lines, chunkStartLineIndex, lineIndex - 1, maxChunkChars, chunks);

                chunkStartLineIndex = lineIndex;

                chunkChars = 0;

            }

            if (chunkChars > 0 && chunkChars + line.Length > maxChunkChars)
            {

                AddRange(content, lines, chunkStartLineIndex, lineIndex - 1, maxChunkChars, chunks);

                chunkStartLineIndex = lineIndex;

                chunkChars = 0;

            }

            chunkChars += line.Length;

        }

        if (chunkStartLineIndex < lines.Count)
        {

            AddRange(content, lines, chunkStartLineIndex, lines.Count - 1, maxChunkChars, chunks);

        }

        return [.. chunks];

    }

    private static void AddRange(
        string content,
        List<LineSlice> lines,
        int startLineIndex,
        int endLineIndex,
        int maxChunkChars,
        List<Chunk> chunks)
    {

        int rangeStart = lines[startLineIndex].Offset;

        LineSlice endSlice = lines[endLineIndex];

        int rangeEnd = endSlice.Offset + endSlice.Length;

        int remaining = rangeEnd - rangeStart;

        int offset = rangeStart;

        int startLine = startLineIndex + 1;

        while (remaining > 0)
        {

            int length = Math.Min(remaining, maxChunkChars);

            if (length < remaining
                && length > 0
                && char.IsHighSurrogate(content[offset + length - 1])
                && char.IsLowSurrogate(content[offset + length]))
            {

                length--;

            }

            if (length == 0)
            {

                length = Math.Min(remaining, 2);

            }

            int endLine = startLine + CountNewlines(content.AsSpan(offset, length));

            if (length > 0 && content[offset + length - 1] == '\n')
            {

                endLine--;

            }

            chunks.Add(new Chunk(
                content.Substring(offset, length),
                offset,
                startLine,
                Math.Max(startLine, endLine)));

            startLine += CountNewlines(content.AsSpan(offset, length));

            offset += length;

            remaining -= length;

        }

    }

    private static List<LineSlice> ReadLines(string content)
    {

        List<LineSlice> lines = [];

        int offset = 0;

        while (offset < content.Length)
        {

            int newline = content.IndexOf('\n', offset);

            if (newline < 0)
            {

                lines.Add(new LineSlice(offset, content.Length - offset, content.Length - offset));

                break;

            }

            int contentLength = newline - offset;

            if (contentLength > 0 && content[newline - 1] == '\r')
            {

                contentLength--;

            }

            lines.Add(new LineSlice(offset, newline - offset + 1, contentLength));

            offset = newline + 1;

        }

        if (content.Length > 0 && content[^1] == '\n')
        {

            lines.Add(new LineSlice(content.Length, 0, 0));

        }

        return lines;

    }

    private static bool IsStableBoundary(ReadOnlySpan<char> line, string extension)
    {

        ReadOnlySpan<char> trimmed = line.Trim();

        if (trimmed.IsEmpty)
        {

            return false;

        }

        if ((extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
            && trimmed[0] == '#')
        {

            return true;

        }

        foreach (string prefix in DeclarationPrefixes)
        {

            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {

                return true;

            }

        }

        return false;

    }

    private static int CountNewlines(ReadOnlySpan<char> content)
    {

        int count = 0;

        foreach (char value in content)
        {

            if (value == '\n')
            {

                count++;

            }

        }

        return count;

    }

    private readonly record struct LineSlice(int Offset, int Length, int ContentLength);

}
