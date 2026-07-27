using System.Collections.ObjectModel;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal enum WorkspaceTextRejection
{
    UnsupportedEncoding,
    InvalidUtf8,
    BinaryContent,
}

internal sealed class WorkspaceTextDecodingException(
    WorkspaceTextRejection rejection,
    string message,
    Exception? innerException = null)
    : IOException(message, innerException)
{

    public WorkspaceTextRejection Rejection { get; } = rejection;

}

internal readonly record struct WorkspaceTextLine(string Text, string Delimiter);

/// <summary>
/// Strict UTF-8 logical text that preserves every existing delimiter and optional UTF-8 BOM.
/// Inserted logical lines use the dominant existing delimiter (first observed wins a tie);
/// files without any delimiter use LF deterministically.
/// </summary>
internal sealed class WorkspaceTextFile
{

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];

    private readonly ReadOnlyCollection<WorkspaceTextLine> _lines;

    private WorkspaceTextFile(
        bool hasUtf8Bom,
        IReadOnlyList<WorkspaceTextLine> lines,
        string dominantNewline)
        : this(
            hasUtf8Bom,
            lines.ToList(),
            dominantNewline)
    {
    }

    private WorkspaceTextFile(
        bool hasUtf8Bom,
        List<WorkspaceTextLine> lines,
        string dominantNewline)
    {

        HasUtf8Bom = hasUtf8Bom;

        _lines = lines.AsReadOnly();

        DominantNewline = dominantNewline;

    }

    internal bool HasUtf8Bom { get; }

    internal IReadOnlyList<WorkspaceTextLine> Lines => _lines;

    internal string DominantNewline { get; }

    internal static WorkspaceTextFile CreateEmpty() =>
        new(
            hasUtf8Bom: false,
            lines: Array.Empty<WorkspaceTextLine>(),
            dominantNewline: "\n");

    internal static WorkspaceTextFile Decode(
        ReadOnlySpan<byte> bytes,
        Action? checkpoint = null)
    {

        checkpoint?.Invoke();

        if (HasUnsupportedBom(bytes))
        {

            throw new WorkspaceTextDecodingException(
                WorkspaceTextRejection.UnsupportedEncoding,
                "Workspace coding tools accept only strict UTF-8 text with an optional UTF-8 BOM.");

        }

        bool hasUtf8Bom = bytes.StartsWith(Utf8Preamble);

        ReadOnlySpan<byte> payload = hasUtf8Bom
            ? bytes[Utf8Preamble.Length..]
            : bytes;

        if (payload.StartsWith(Utf8Preamble))
        {

            throw new WorkspaceTextDecodingException(
                WorkspaceTextRejection.UnsupportedEncoding,
                "A UTF-8 BOM is allowed only once at the beginning of a file.");

        }

        string text;

        try
        {

            text = DecodeUtf8(payload, checkpoint);

        }
        catch (DecoderFallbackException exception)
        {

            throw new WorkspaceTextDecodingException(
                WorkspaceTextRejection.InvalidUtf8,
                "The file is not valid strict UTF-8.",
                exception);

        }

        List<WorkspaceTextLine> lines = ParseLines(text, checkpoint);

        return new WorkspaceTextFile(
            hasUtf8Bom,
            lines,
            SelectDominantNewline(lines, checkpoint));

    }

    internal byte[] Encode()
    {

        StringBuilder builder = new();

        foreach (WorkspaceTextLine line in _lines)
        {

            _ = builder.Append(line.Text).Append(line.Delimiter);

        }

        byte[] payload = StrictUtf8.GetBytes(builder.ToString());

        if (!HasUtf8Bom)
        {

            return payload;

        }

        byte[] encoded = new byte[Utf8Preamble.Length + payload.Length];

        Utf8Preamble.CopyTo(encoded, 0);

        payload.CopyTo(encoded, Utf8Preamble.Length);

        return encoded;

    }

    internal long GetEncodedByteCount(Action? checkpoint = null)
    {

        long count = HasUtf8Bom ? Utf8Preamble.Length : 0;

        foreach (WorkspaceTextLine line in _lines)
        {

            checkpoint?.Invoke();

            count = checked(count + StrictUtf8.GetByteCount(line.Text));

            count = checked(count + StrictUtf8.GetByteCount(line.Delimiter));

        }

        return count;

    }

    internal WorkspaceTextFile InsertLines(
        int index,
        IReadOnlyList<string> insertedLines)
    {

        ArgumentNullException.ThrowIfNull(insertedLines);

        if (index < 0 || index > _lines.Count)
        {

            throw new ArgumentOutOfRangeException(nameof(index));

        }

        if (insertedLines.Count == 0)
        {

            return this;

        }

        foreach (string line in insertedLines)
        {

            ArgumentNullException.ThrowIfNull(line);

            if (line.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {

                throw new ArgumentException(
                    "Inserted values must be individual non-binary logical lines.",
                    nameof(insertedLines));

            }

        }

        List<WorkspaceTextLine> edited = [.. _lines];

        if (index < edited.Count)
        {

            edited.InsertRange(
                index,
                insertedLines.Select(line => new WorkspaceTextLine(line, DominantNewline)));

        }
        else if (edited.Count == 0)
        {

            for (int lineIndex = 0; lineIndex < insertedLines.Count; lineIndex++)
            {

                string delimiter = lineIndex == insertedLines.Count - 1
                    ? string.Empty
                    : DominantNewline;

                edited.Add(new WorkspaceTextLine(insertedLines[lineIndex], delimiter));

            }

        }
        else
        {

            bool endedWithNewline = edited[^1].Delimiter.Length > 0;

            if (!endedWithNewline)
            {

                edited[^1] = edited[^1] with { Delimiter = DominantNewline };

            }

            for (int lineIndex = 0; lineIndex < insertedLines.Count; lineIndex++)
            {

                bool insertedLineKeepsFinalNewline =
                    endedWithNewline || lineIndex < insertedLines.Count - 1;

                edited.Add(
                    new WorkspaceTextLine(
                        insertedLines[lineIndex],
                        insertedLineKeepsFinalNewline ? DominantNewline : string.Empty));

            }

        }

        return new WorkspaceTextFile(
            HasUtf8Bom,
            edited,
            DominantNewline);

    }

    internal WorkspaceTextFile WithLines(
        IReadOnlyList<WorkspaceTextLine> lines,
        Action? checkpoint = null)
    {

        ArgumentNullException.ThrowIfNull(lines);

        foreach (WorkspaceTextLine line in lines)
        {
            checkpoint?.Invoke();

            ArgumentNullException.ThrowIfNull(line.Text);

            ArgumentNullException.ThrowIfNull(line.Delimiter);

            if (line.Text.IndexOfAny(['\r', '\n', '\0']) >= 0
                || line.Delimiter is not ("" or "\r" or "\n" or "\r\n"))
            {
                throw new ArgumentException(
                    "Logical text lines must contain valid text and a supported delimiter.",
                    nameof(lines));
            }
        }

        return new WorkspaceTextFile(
            HasUtf8Bom,
            lines,
            DominantNewline);

    }

    private static string DecodeUtf8(
        ReadOnlySpan<byte> payload,
        Action? checkpoint)
    {

        if (payload.Length == 0)
        {

            checkpoint?.Invoke();

            return string.Empty;

        }

        Decoder decoder = StrictUtf8.GetDecoder();
        StringBuilder builder = new(payload.Length);
        char[] chars = new char[4096];
        int offset = 0;

        while (offset < payload.Length)
        {

            checkpoint?.Invoke();

            int byteCount = Math.Min(4096, payload.Length - offset);
            bool flush = offset + byteCount == payload.Length;

            decoder.Convert(
                payload.Slice(offset, byteCount),
                chars.AsSpan(),
                flush,
                out int bytesUsed,
                out int charsUsed,
                out _);

            if (bytesUsed == 0)
            {

                throw new DecoderFallbackException(
                    "Strict UTF-8 decoding made no forward progress.");

            }

            ReadOnlySpan<char> decoded = chars.AsSpan(0, charsUsed);

            if (decoded.Contains('\0'))
            {

                throw new WorkspaceTextDecodingException(
                    WorkspaceTextRejection.BinaryContent,
                    "The file contains NUL bytes and is treated as binary.");

            }

            _ = builder.Append(decoded);
            offset += bytesUsed;

        }

        checkpoint?.Invoke();

        return builder.ToString();

    }

    private static List<WorkspaceTextLine> ParseLines(
        string text,
        Action? checkpoint)
    {

        List<WorkspaceTextLine> lines = [];

        int lineStart = 0;

        for (int index = 0; index < text.Length; index++)
        {

            if ((index & 0xFFF) == 0)
            {

                checkpoint?.Invoke();

            }

            char character = text[index];

            if (character is not ('\r' or '\n'))
            {

                continue;

            }

            string delimiter;

            if (character == '\r'
                && index + 1 < text.Length
                && text[index + 1] == '\n')
            {

                delimiter = "\r\n";

                index++;

            }
            else
            {

                delimiter = character.ToString();

            }

            int delimiterStart = index - delimiter.Length + 1;

            lines.Add(
                new WorkspaceTextLine(
                    text[lineStart..delimiterStart],
                    delimiter));

            lineStart = index + 1;

        }

        if (lineStart < text.Length)
        {

            lines.Add(new WorkspaceTextLine(text[lineStart..], string.Empty));

        }

        return lines;

    }

    private static string SelectDominantNewline(
        IReadOnlyList<WorkspaceTextLine> lines,
        Action? checkpoint)
    {

        Dictionary<string, (int Count, int FirstIndex)> counts =
            new(StringComparer.Ordinal);

        for (int index = 0; index < lines.Count; index++)
        {

            if ((index & 0xFFF) == 0)
            {

                checkpoint?.Invoke();

            }

            string delimiter = lines[index].Delimiter;

            if (delimiter.Length == 0)
            {

                continue;

            }

            if (counts.TryGetValue(delimiter, out (int Count, int FirstIndex) current))
            {

                counts[delimiter] = (current.Count + 1, current.FirstIndex);

            }
            else
            {

                counts[delimiter] = (1, index);

            }

        }

        return counts
            .OrderByDescending(item => item.Value.Count)
            .ThenBy(item => item.Value.FirstIndex)
            .Select(item => item.Key)
            .FirstOrDefault() ?? "\n";

    }

    private static bool HasUnsupportedBom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })
        || bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })
        || bytes.StartsWith(new byte[] { 0xFF, 0xFE })
        || bytes.StartsWith(new byte[] { 0xFE, 0xFF });

}
