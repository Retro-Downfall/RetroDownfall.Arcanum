using System.Net;

using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal enum SessionAttachmentExtractionStatus
{

    Extracted,

    NotEligible,

    Failed,

}

internal sealed record SessionAttachmentExtractionResult(
    SessionAttachmentExtractionStatus Status,
    string Text,
    bool WasTruncated,
    string? FailureReason = null);

internal sealed record SessionAttachmentTextChunk(
    int ChunkIndex,
    int CharacterStart,
    int CharacterEnd,
    int StartLine,
    int EndLine,
    string Text);

internal static class SessionAttachmentTextExtractor
{

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> SupportedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {

        "application/json",

        "application/ld+json",

        "application/toml",

        "application/xml",

        "application/x-httpd-php",

        "application/x-javascript",

        "application/x-ndjson",

        "application/x-sh",

        "application/x-yaml",

        "application/yaml",

        "text/csv",

        "text/javascript",

        "text/markdown",

        "text/plain",

        "text/tab-separated-values",

        "text/x-c",

        "text/x-c++",

        "text/x-csharp",

        "text/x-go",

        "text/x-java-source",

        "text/x-kotlin",

        "text/x-log",

        "text/x-python",

        "text/x-ruby",

        "text/x-rust",

        "text/x-shellscript",

        "text/x-sql",

        "text/xml",

        "text/yaml",

    };

    public static SessionAttachmentExtractionResult Extract(
        ReadOnlySpan<byte> bytes,
        string mimeType,
        string fileName,
        int maxCharacters)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (maxCharacters <= 0)
        {

            throw new ArgumentOutOfRangeException(nameof(maxCharacters));

        }

        string normalizedMimeType = mimeType.Split(';', 2)[0].Trim();

        bool isHtml = normalizedMimeType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || normalizedMimeType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

        if (!isHtml && !IsSupportedText(normalizedMimeType))
        {

            return new SessionAttachmentExtractionResult(
                SessionAttachmentExtractionStatus.NotEligible,
                string.Empty,
                WasTruncated: false);

        }

        string decoded;

        try
        {

            decoded = StrictUtf8.GetString(bytes);

        }
        catch (DecoderFallbackException)
        {

            return new SessionAttachmentExtractionResult(
                SessionAttachmentExtractionStatus.Failed,
                string.Empty,
                WasTruncated: false,
                "Attachment is not valid UTF-8.");

        }

        if (decoded.IndexOf('\0', StringComparison.Ordinal) >= 0)
        {

            return new SessionAttachmentExtractionResult(
                SessionAttachmentExtractionStatus.NotEligible,
                string.Empty,
                WasTruncated: false,
                "Attachment contains binary NUL bytes.");

        }

        string extracted = isHtml
            ? ExtractVisibleHtmlText(decoded)
            : NormalizeNewlines(decoded);

        bool truncated = extracted.Length > maxCharacters;

        if (truncated)
        {

            int length = maxCharacters;

            if (length > 0 && length < extracted.Length && char.IsHighSurrogate(extracted[length - 1]))
            {

                length--;

            }

            extracted = extracted[..length];

        }

        return new SessionAttachmentExtractionResult(
            SessionAttachmentExtractionStatus.Extracted,
            extracted,
            truncated);

    }

    private static bool IsSupportedText(string mimeType) => SupportedMimeTypes.Contains(mimeType);

    private static string ExtractVisibleHtmlText(string html)
    {

        StringBuilder visible = new(html.Length);

        bool inTag = false;

        string? suppressedTag = null;

        for (int i = 0; i < html.Length; i++)
        {

            char current = html[i];

            if (!inTag && current == '<')
            {

                int close = html.IndexOf('>', i + 1);

                if (close < 0)
                {

                    break;

                }

                string tagBody = html[(i + 1)..close].Trim();

                bool closing = tagBody.StartsWith("/", StringComparison.Ordinal);

                string tagName = ReadTagName(closing ? tagBody[1..] : tagBody);

                if (closing && suppressedTag is not null
                    && tagName.Equals(suppressedTag, StringComparison.OrdinalIgnoreCase))
                {

                    suppressedTag = null;

                }
                else if (!closing && suppressedTag is null
                    && (tagName.Equals("script", StringComparison.OrdinalIgnoreCase)
                        || tagName.Equals("style", StringComparison.OrdinalIgnoreCase)))
                {

                    suppressedTag = tagName;

                }

                if (suppressedTag is null && IsBlockBoundaryTag(tagName))
                {

                    AppendWhitespace(visible);

                }

                i = close;

                continue;

            }

            if (!inTag && suppressedTag is null)
            {

                visible.Append(current);

            }

        }

        string decoded = WebUtility.HtmlDecode(visible.ToString());

        return CollapseWhitespace(NormalizeNewlines(decoded));

    }

    private static string ReadTagName(string tagBody)
    {

        int length = 0;

        while (length < tagBody.Length
            && (char.IsAsciiLetterOrDigit(tagBody[length]) || tagBody[length] is '-' or ':'))
        {

            length++;

        }

        return tagBody[..length];

    }

    private static bool IsBlockBoundaryTag(string tagName) => tagName.Equals("br", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("div", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("h1", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("h2", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("h3", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("h4", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("h5", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("h6", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("li", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("p", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("section", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("table", StringComparison.OrdinalIgnoreCase)
        || tagName.Equals("tr", StringComparison.OrdinalIgnoreCase);

    private static void AppendWhitespace(StringBuilder builder)
    {

        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
        {

            builder.Append(' ');

        }

    }

    private static string CollapseWhitespace(string value)
    {

        StringBuilder collapsed = new(value.Length);

        bool previousWhitespace = true;

        foreach (char current in value)
        {

            bool whitespace = char.IsWhiteSpace(current);

            if (whitespace)
            {

                if (!previousWhitespace)
                {

                    collapsed.Append(' ');

                }

            }
            else
            {

                collapsed.Append(current);

            }

            previousWhitespace = whitespace;

        }

        return collapsed.ToString().Trim();

    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

}

internal static class SessionAttachmentChunker
{

    public static SessionAttachmentTextChunk[] Chunk(
        string text,
        int chunkSizeCharacters,
        int overlapCharacters,
        int maxChunks)
    {

        ArgumentNullException.ThrowIfNull(text);

        if (chunkSizeCharacters <= 0)
        {

            throw new ArgumentOutOfRangeException(nameof(chunkSizeCharacters));

        }

        if (overlapCharacters < 0 || overlapCharacters >= chunkSizeCharacters)
        {

            throw new ArgumentOutOfRangeException(nameof(overlapCharacters));

        }

        if (maxChunks <= 0)
        {

            throw new ArgumentOutOfRangeException(nameof(maxChunks));

        }

        if (text.Length == 0)
        {

            return [];

        }

        int step = chunkSizeCharacters - overlapCharacters;

        List<SessionAttachmentTextChunk> chunks = new(Math.Min(maxChunks, 16));

        for (int offset = 0; offset < text.Length && chunks.Count < maxChunks; offset += step)
        {

            int start = offset;

            if (start > 0 && char.IsLowSurrogate(text[start]))
            {

                start++;

            }

            if (start >= text.Length)
            {

                break;

            }

            int end = Math.Min(text.Length, offset + chunkSizeCharacters);

            if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1]))
            {

                end--;

            }

            if (end <= start)
            {

                continue;

            }

            int startLine = CountLineAt(text, start);

            int endLine = CountLineAt(text, Math.Max(start, end - 1));

            chunks.Add(new SessionAttachmentTextChunk(
                chunks.Count,
                start,
                end,
                startLine,
                endLine,
                text[start..end]));

            if (end == text.Length)
            {

                break;

            }

        }

        return [.. chunks];

    }

    private static int CountLineAt(string text, int characterOffset)
    {

        int line = 1;

        for (int i = 0; i < characterOffset; i++)
        {

            if (text[i] == '\n')
            {

                line++;

            }

        }

        return line;

    }

}
