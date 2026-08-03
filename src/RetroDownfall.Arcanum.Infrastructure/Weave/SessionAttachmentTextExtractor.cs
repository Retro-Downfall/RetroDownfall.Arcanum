using System.Buffers;

using System.Net;

using System.Runtime.CompilerServices;

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

internal sealed class SessionAttachmentExtractionException(
    SessionAttachmentExtractionStatus status,
    string? failureReason = null) : Exception(
        failureReason ?? "Attachment text extraction failed.")
{

    public SessionAttachmentExtractionStatus Status { get; } = status;

    public string? FailureReason { get; } = failureReason;

}

internal static class SessionAttachmentTextExtractor
{

    private const int StreamingCharacterBufferSize = 4096;

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
        string fileName)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

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

        return new SessionAttachmentExtractionResult(
            SessionAttachmentExtractionStatus.Extracted,
            extracted,
            WasTruncated: false);

    }

    public static async IAsyncEnumerable<SessionAttachmentTextChunk> ReadChunksAsync(
        Stream stream,
        string mimeType,
        string fileName,
        int chunkSizeCharacters,
        int overlapCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(stream);

        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string normalizedMimeType = mimeType.Split(';', 2)[0].Trim();

        bool isHtml = normalizedMimeType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || normalizedMimeType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

        if (!isHtml && !IsSupportedText(normalizedMimeType))
        {

            throw new SessionAttachmentExtractionException(
                SessionAttachmentExtractionStatus.NotEligible);

        }

        SessionAttachmentChunkAssembler assembler = new(
            chunkSizeCharacters,
            overlapCharacters);

        SessionAttachmentStreamingTextTransformer transformer = new(
            isHtml,
            assembler.Append);

        char[] buffer = ArrayPool<char>.Shared.Rent(StreamingCharacterBufferSize);

        try
        {

            using StreamReader reader = new(
                stream,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                StreamingCharacterBufferSize,
                leaveOpen: true);

            while (true)
            {

                int read;

                try
                {

                    read = await reader
                        .ReadAsync(
                            buffer.AsMemory(0, StreamingCharacterBufferSize),
                            cancellationToken)
                        .ConfigureAwait(false);

                }
                catch (DecoderFallbackException)
                {

                    throw new SessionAttachmentExtractionException(
                        SessionAttachmentExtractionStatus.Failed,
                        "Attachment is not valid UTF-8.");

                }

                if (read == 0)
                {

                    break;

                }

                for (int index = 0; index < read; index++)
                {

                    char current = buffer[index];

                    if (current == '\0')
                    {

                        throw new SessionAttachmentExtractionException(
                            SessionAttachmentExtractionStatus.NotEligible,
                            "Attachment contains binary NUL bytes.");

                    }

                    transformer.Append(current);

                }

                while (assembler.TryTakeChunk(completed: false, out SessionAttachmentTextChunk chunk))
                {

                    yield return chunk;

                }

            }

            transformer.Complete();

            while (assembler.TryTakeChunk(completed: true, out SessionAttachmentTextChunk chunk))
            {

                yield return chunk;

            }

        }
        finally
        {

            ArrayPool<char>.Shared.Return(buffer, clearArray: true);

        }

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

internal sealed class SessionAttachmentStreamingTextTransformer(
    bool html,
    Action<char> emit)
{

    private const int MaxHtmlCharacterReferenceLength = 64;

    private const int MaxHtmlTagPrefixLength = 128;

    private readonly StringBuilder _entity = new(MaxHtmlCharacterReferenceLength);

    private readonly StringBuilder _tagPrefix = new(MaxHtmlTagPrefixLength);

    private bool _html = html;

    private bool _inEntity;

    private bool _inTag;

    private bool _hasVisibleOutput;

    private bool _pendingCarriageReturn;

    private bool _pendingVisibleWhitespace;

    private string? _suppressedTag;

    public void Append(char current)
    {

        if (_html)
        {

            AppendHtml(current);

            return;

        }

        AppendNormalized(current);

    }

    public void Complete()
    {

        if (_html)
        {

            FlushEntity();

            return;

        }

        if (_pendingCarriageReturn)
        {

            emit('\n');

            _pendingCarriageReturn = false;

        }

    }

    private void AppendNormalized(char current)
    {

        if (current == '\r')
        {

            if (_pendingCarriageReturn)
            {

                emit('\n');

            }

            _pendingCarriageReturn = true;

            return;

        }

        if (_pendingCarriageReturn)
        {

            emit('\n');

            _pendingCarriageReturn = false;

            if (current == '\n')
            {

                return;

            }

        }

        emit(current);

    }

    private void AppendHtml(char current)
    {

        if (_inTag)
        {

            if (current == '>')
            {

                CompleteTag();

                return;

            }

            if (_tagPrefix.Length < MaxHtmlTagPrefixLength)
            {

                _tagPrefix.Append(current);

            }

            return;

        }

        if (current == '<')
        {

            FlushEntity();

            _inTag = true;

            _tagPrefix.Clear();

            return;

        }

        if (_suppressedTag is null)
        {

            AppendHtmlVisible(current);

        }

    }

    private void CompleteTag()
    {

        string tagBody = _tagPrefix.ToString().Trim();

        bool closing = tagBody.StartsWith("/", StringComparison.Ordinal);

        string tagName = ReadTagName(closing ? tagBody[1..] : tagBody);

        if (closing && _suppressedTag is not null
            && tagName.Equals(_suppressedTag, StringComparison.OrdinalIgnoreCase))
        {

            _suppressedTag = null;

        }
        else if (!closing && _suppressedTag is null
            && (tagName.Equals("script", StringComparison.OrdinalIgnoreCase)
                || tagName.Equals("style", StringComparison.OrdinalIgnoreCase)))
        {

            _suppressedTag = tagName;

        }

        if (_suppressedTag is null && IsBlockBoundaryTag(tagName))
        {

            AppendDecodedVisible(' ');

        }

        _inTag = false;

        _tagPrefix.Clear();

    }

    private void AppendHtmlVisible(char current)
    {

        if (!_inEntity)
        {

            if (current == '&')
            {

                _inEntity = true;

                _entity.Clear();

                _entity.Append(current);

                return;

            }

            AppendDecodedVisible(current);

            return;

        }

        _entity.Append(current);

        if (current == ';')
        {

            FlushEntity();

            return;

        }

        if (_entity.Length >= MaxHtmlCharacterReferenceLength)
        {

            foreach (char raw in _entity.ToString())
            {

                AppendDecodedVisible(raw);

            }

            _entity.Clear();

            _inEntity = false;

        }

    }

    private void FlushEntity()
    {

        if (!_inEntity)
        {

            return;

        }

        string decoded = WebUtility.HtmlDecode(_entity.ToString());

        foreach (char current in decoded)
        {

            AppendDecodedVisible(current);

        }

        _entity.Clear();

        _inEntity = false;

    }

    private void AppendDecodedVisible(char current)
    {

        if (char.IsWhiteSpace(current))
        {

            if (_hasVisibleOutput)
            {

                _pendingVisibleWhitespace = true;

            }

            return;

        }

        if (_pendingVisibleWhitespace)
        {

            emit(' ');

            _pendingVisibleWhitespace = false;

        }

        emit(current);

        _hasVisibleOutput = true;

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

}

internal sealed class SessionAttachmentChunkAssembler
{

    private readonly StringBuilder _buffer = new();

    private readonly int _chunkSizeCharacters;

    private readonly int _stepCharacters;

    private int _bufferStart;

    private int _bufferStartLine = 1;

    private int _chunkIndex;

    private int _lastEmittedEnd = -1;

    private int _nextOffset;

    private int _totalCharacters;

    public SessionAttachmentChunkAssembler(
        int chunkSizeCharacters,
        int overlapCharacters)
    {

        if (chunkSizeCharacters <= 0)
        {

            throw new ArgumentOutOfRangeException(nameof(chunkSizeCharacters));

        }

        if (overlapCharacters < 0 || overlapCharacters >= chunkSizeCharacters)
        {

            throw new ArgumentOutOfRangeException(nameof(overlapCharacters));

        }

        _chunkSizeCharacters = chunkSizeCharacters;

        _stepCharacters = chunkSizeCharacters - overlapCharacters;

    }

    public void Append(char current)
    {

        if (_totalCharacters == int.MaxValue)
        {

            throw new SessionAttachmentExtractionException(
                SessionAttachmentExtractionStatus.Failed,
                "Attachment text exceeds the index coordinate boundary of 2,147,483,647 characters.");

        }

        _buffer.Append(current);

        _totalCharacters++;

    }

    public bool TryTakeChunk(
        bool completed,
        out SessionAttachmentTextChunk chunk)
    {

        chunk = null!;

        if (_totalCharacters == 0
            || (completed && _lastEmittedEnd == _totalCharacters)
            || _nextOffset >= _totalCharacters)
        {

            return false;

        }

        long desiredEnd = (long)_nextOffset + _chunkSizeCharacters;

        if (!completed && desiredEnd > _totalCharacters)
        {

            return false;

        }

        int start = _nextOffset;

        int relativeStart = start - _bufferStart;

        if (start > 0 && relativeStart < _buffer.Length && char.IsLowSurrogate(_buffer[relativeStart]))
        {

            start++;

            relativeStart++;

        }

        if (start >= _totalCharacters)
        {

            return false;

        }

        int end = (int)Math.Min(_totalCharacters, desiredEnd);

        int relativeEnd = end - _bufferStart;

        if ((!completed || end < _totalCharacters)
            && relativeEnd > relativeStart
            && char.IsHighSurrogate(_buffer[relativeEnd - 1]))
        {

            end--;

            relativeEnd--;

        }

        if (end <= start)
        {

            AdvanceWindow();

            return TryTakeChunk(completed, out chunk);

        }

        int startLine = _bufferStartLine + CountNewlines(0, relativeStart);

        int endLine = _bufferStartLine + CountNewlines(0, Math.Max(relativeStart, relativeEnd - 1));

        chunk = new SessionAttachmentTextChunk(
            _chunkIndex,
            start,
            end,
            startLine,
            endLine,
            _buffer.ToString(relativeStart, relativeEnd - relativeStart));

        if (_chunkIndex == int.MaxValue)
        {

            throw new SessionAttachmentExtractionException(
                SessionAttachmentExtractionStatus.Failed,
                "Attachment text exceeds the index chunk-coordinate boundary.");

        }

        _chunkIndex++;

        _lastEmittedEnd = end;

        AdvanceWindow();

        return true;

    }

    private void AdvanceWindow()
    {

        _nextOffset = (int)Math.Min(
            int.MaxValue,
            (long)_nextOffset + _stepCharacters);

        int discard = Math.Min(_buffer.Length, Math.Max(0, _nextOffset - _bufferStart));

        if (discard == 0)
        {

            return;

        }

        _bufferStartLine += CountNewlines(0, discard);

        _buffer.Remove(0, discard);

        _bufferStart += discard;

    }

    private int CountNewlines(int start, int endExclusive)
    {

        int count = 0;

        for (int index = start; index < endExclusive; index++)
        {

            if (_buffer[index] == '\n')
            {

                count++;

            }

        }

        return count;

    }

}

internal static class SessionAttachmentChunker
{

    public static SessionAttachmentTextChunk[] Chunk(
        string text,
        int chunkSizeCharacters,
        int overlapCharacters)
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

        if (text.Length == 0)
        {

            return [];

        }

        int step = chunkSizeCharacters - overlapCharacters;

        List<SessionAttachmentTextChunk> chunks = [];

        for (int offset = 0; offset < text.Length; offset += step)
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
