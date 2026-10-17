using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using System.Net;
using System.Text.Json;
using System.Security;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Tests.Packaging;

internal static class LocalOllamaQualificationGuards
{
    public const string VisionAttachmentName = "opaque-input.dat";

    public const string VisionPrompt =
        "The current pinned durable-fact file supersedes the older token in this transcript. "
        + "Inspect the explicitly attached image and recall the conversation marker. "
        + "Reply with exactly MARKER=<conversation marker>; TOKEN=<corrected file token>; "
        + "OBJECT=<object>; COLOR=<dominant object color>; SHAPE=<shape>; "
        + "SIDES=<number of sides>; TEXT=<visible text> and no other text.";

    private static readonly string[] ForbiddenVisionAnswerTokens =
    [
        "stop",
        "sign",
        "red",
        "octagon",
        "octagonal",
        "eight",
        "8",
    ];

    internal sealed record PerplexitySearchEvidence(
        int CitationIndex,
        string CitationUrl,
        string CallContent,
        string ResultContent,
        Guid SearchUserId,
        Guid SearchReplyId,
        Guid CallEntryId,
        Guid ResultEntryId);

    internal sealed record NativeSearchEvidence(
        string CallId,
        string ArgumentsJson,
        string ResultJson,
        string Answer,
        int CitationIndex,
        string CitationUrl);

    internal static async Task<NativeSearchEvidence?> ReadNativeSearchAsync(
        TextReader reader,
        Guid sessionId,
        string expectedQuery,
        string searchMarker,
        CancellationToken cancellationToken)
    {
        const int maxFrameCharacters = 1024 * 1024;

        StringBuilder pending = new();

        StringBuilder tokens = new();

        IntelligenceToolCallEvent? call = null;

        string? resultJson = null;

        NativeSearchEvidence? terminal = null;

        char[] buffer = new char[4096];

        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);

                if (read == 0)
                {
                    return string.IsNullOrWhiteSpace(pending.ToString()) ? terminal : null;
                }

                for (int index = 0; index < read; index++)
                {
                    char character = buffer[index];

                    if (character != '\n')
                    {
                        pending.Append(character);

                        if (pending.Length > maxFrameCharacters)
                        {
                            return null;
                        }

                        continue;
                    }

                    string line = pending.ToString();

                    pending.Clear();

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    using JsonDocument document = JsonDocument.Parse(line);

                    JsonElement root = document.RootElement;

                    if (!HasUniqueObjectProperties(root)
                        || root.ValueKind != JsonValueKind.Object
                        || root.GetProperty("type").ValueKind != JsonValueKind.String
                        || root.GetProperty("message").ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    string? type = root.GetProperty("type").GetString();

                    if (type is not ("status" or "sessionBound" or "conversationBound" or "token" or "reasoning"
                        or "context" or "result" or "error" or "toolCall" or "toolResult" or "warded"
                        or "wardResolved" or "toolError" or "attachmentRefreshed"))
                    {
                        return null;
                    }

                    IntelligenceEvent frame = JsonSerializer.Deserialize(line, ArcanumJsonContext.Default.IntelligenceEvent)!;

                    if (frame.ToolCall is not null
                        && frame.Type is not (IntelligenceEventType.ToolCall or IntelligenceEventType.ToolResult))
                    {
                        return null;
                    }

                    switch (frame.Type)
                    {
                        case IntelligenceEventType.ToolCall:
                            if (terminal is not null || call is not null || resultJson is not null
                                || frame.ToolCall is not { } proposed
                                || string.IsNullOrWhiteSpace(proposed.CallId)
                                || proposed.Name != "web_search" || proposed.Index != 0
                                || frame.Message != proposed.Name || frame.Data != proposed.ArgumentsJson
                                || string.IsNullOrWhiteSpace(proposed.ArgumentsJson)
                                || !IsSearchArguments(proposed.ArgumentsJson, expectedQuery))
                            {
                                return null;
                            }

                            call = proposed;

                            break;

                        case IntelligenceEventType.ToolResult:
                            if (terminal is not null || call is null || resultJson is not null
                                || frame.ToolCall is not { } completed
                                || completed.CallId != call.CallId || completed.Name != call.Name
                                || completed.Index != call.Index || completed.ArgumentsJson != call.ArgumentsJson
                                || frame.Message != "web_search" || frame.Data is null
                                || !TryReadPerplexityResult(frame.Data, out _, out _))
                            {
                                return null;
                            }

                            resultJson = frame.Data;

                            break;

                        case IntelligenceEventType.Token:
                            if (terminal is not null || frame.Data is null || frame.Message != frame.Data)
                            {
                                return null;
                            }

                            tokens.Append(frame.Data);

                            if (tokens.Length > maxFrameCharacters)
                            {
                                return null;
                            }

                            break;

                        case IntelligenceEventType.Result:
                            if (terminal is not null || call is null || resultJson is null
                                || tokens.ToString() != frame.Message
                                || frame.FinishReason != "stop"
                                || !long.TryParse(frame.Data, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                                || !TryReadPerplexityResult(resultJson, out int citationIndex, out string? citationUrl))
                            {
                                return null;
                            }

                            string answer = PublishedArcanumOllamaQualificationTests.NormalizeModelAnswer(frame.Message);

                            if (answer != $"SEARCH_READY; MARKER={searchMarker}; CITATION_INDEX={citationIndex}; CITATION_URL={citationUrl}")
                            {
                                return null;
                            }

                            terminal = new(call.CallId, call.ArgumentsJson, resultJson, answer, citationIndex, citationUrl!);

                            break;

                        case IntelligenceEventType.SessionBound:
                        case IntelligenceEventType.ConversationBound:
                            if (!Guid.TryParse(frame.Data, out Guid bound) || bound != sessionId)
                            {
                                return null;
                            }

                            break;

                        case IntelligenceEventType.Error:
                        case IntelligenceEventType.ToolError:
                            return null;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or DecoderFallbackException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    internal static bool SameDurableRows(IReadOnlyList<JsonElement> before, IReadOnlyList<JsonElement> after) =>
        before.Count == after.Count
            && HasUniqueEntryIds(before)
            && HasUniqueEntryIds(after)
            && before.Zip(after).All(pair => JsonElement.DeepEquals(pair.First, pair.Second));

    internal static bool TryReadLocalTranscript(
        IReadOnlyList<JsonElement> rows,
        Guid sessionId,
        IReadOnlyList<string> prompts,
        IReadOnlyList<string> replies)
    {
        if (prompts.Count is < 1 or > 3 || replies.Count != prompts.Count
            || rows.Count != prompts.Count * 2 || !HasUniqueEntryIds(rows))
        {
            return false;
        }

        try
        {
            return Enumerable.Range(0, prompts.Count).All(turn =>
                IsTranscriptRow(rows[turn * 2], sessionId, "user", prompts[turn])
                    && IsTranscriptRow(rows[(turn * 2) + 1], sessionId, "assistant", replies[turn]));
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return false;
        }
    }

    internal static bool TryReadPerplexityTranscript(
        IReadOnlyList<JsonElement> rows,
        Guid sessionId,
        IReadOnlyList<string> prompts,
        IReadOnlyList<string> replies,
        string expectedQuery,
        out PerplexitySearchEvidence? evidence,
        NativeSearchEvidence? transport = null)
    {
        evidence = null;

        if (prompts.Count is < 2 or > 4 || replies.Count != prompts.Count
            || rows.Count != (prompts.Count * 2) + 2 || !HasUniqueEntryIds(rows))
        {
            return false;
        }

        try
        {
            for (int turn = 0; turn < prompts.Count; turn++)
            {
                int offset = turn < 2 ? turn * 2 : (turn * 2) + 2;

                if (!IsTranscriptRow(rows[offset], sessionId, "user", prompts[turn])
                    || !IsTranscriptRow(rows[offset + 1], sessionId, "assistant", replies[turn]))
                {
                    return false;
                }
            }

            JsonElement call = rows[4];

            JsonElement result = rows[5];

            if (!IsTranscriptRow(call, sessionId, "assistant", toolName: "web_search")
                || !IsTranscriptRow(result, sessionId, "system"))
            {
                return false;
            }

            string callContent = call.GetProperty("content").GetString()!;

            string resultContent = result.GetProperty("content").GetString()!;

            const string callPrefix = "[ToolCall: web_search(";

            const string resultPrefix = "[ToolResult: ";

            if (!callContent.StartsWith(callPrefix, StringComparison.Ordinal)
                || !callContent.EndsWith(")]", StringComparison.Ordinal)
                || !resultContent.StartsWith(resultPrefix, StringComparison.Ordinal)
                || !resultContent.EndsWith(']'))
            {
                return false;
            }

            string argumentsJson = callContent[callPrefix.Length..^2];

            string resultJson = resultContent[resultPrefix.Length..^1];

            if (!IsSearchArguments(argumentsJson, expectedQuery)
                || !TryReadPerplexityResult(resultJson, out int firstIndex, out string? firstUrl)
                || !replies[1].EndsWith($"; CITATION_INDEX={firstIndex}; CITATION_URL={firstUrl}", StringComparison.Ordinal)
                || (prompts.Count >= 3
                    && !replies[2].EndsWith($"; CITATION_INDEX={firstIndex}; CITATION_URL={firstUrl}", StringComparison.Ordinal))
                || (transport is not null
                    && (argumentsJson != transport.ArgumentsJson || resultJson != transport.ResultJson
                        || replies[1] != transport.Answer || firstIndex != transport.CitationIndex
                        || firstUrl != transport.CitationUrl)))
            {
                return false;
            }

            evidence = new(firstIndex, firstUrl!, callContent, resultContent,
                rows[2].GetProperty("id").GetGuid(), rows[3].GetProperty("id").GetGuid(),
                call.GetProperty("id").GetGuid(), result.GetProperty("id").GetGuid());

            return true;
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return false;
        }
    }

    private static bool HasUniqueEntryIds(IReadOnlyList<JsonElement> rows)
    {
        HashSet<Guid> ids = [];

        try
        {
            return rows.All(row => HasUniqueObjectProperties(row)
                && row.GetProperty("id").GetGuid() is var id && id != Guid.Empty && ids.Add(id));
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return false;
        }
    }

    private static bool IsSearchArguments(string argumentsJson, string expectedQuery)
    {
        using JsonDocument arguments = JsonDocument.Parse(argumentsJson);

        JsonElement query = arguments.RootElement;

        return query.ValueKind == JsonValueKind.Object
            && HasUniqueObjectProperties(query)
            && query.EnumerateObject().Count() == 1
            && query.GetProperty("query").GetString() == expectedQuery;
    }

    private static bool TryReadPerplexityResult(string resultJson, out int firstIndex, out string? firstUrl)
    {
        firstIndex = 0;

        firstUrl = null;

        using JsonDocument envelope = JsonDocument.Parse(resultJson);

        JsonElement root = envelope.RootElement;

        if (!HasUniqueObjectProperties(root)
            || root.GetProperty("status").GetString() != "ok"
            || root.GetProperty("provider").GetString() != "perplexity"
            || root.GetProperty("model").GetString() is not ("sonar" or "sonar-pro"))
        {
            return false;
        }

        JsonElement data = root.GetProperty("data");

        string? answer = data.GetProperty("answer").GetString();

        const string framing = RetroDownfall.Arcanum.Api.Intelligence.Tools.ArcanumWebSearchTool.UntrustedAnswerFraming;

        if (answer is null || !answer.StartsWith(framing, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(answer[framing.Length..]))
        {
            return false;
        }

        JsonElement citations = data.GetProperty("citations");

        if (citations.ValueKind != JsonValueKind.Array || citations.GetArrayLength() == 0)
        {
            return false;
        }

        int previousIndex = 0;

        foreach (JsonElement citation in citations.EnumerateArray())
        {
            int index = citation.GetProperty("index").GetInt32();

            string? url = citation.GetProperty("url").GetString();

            if (index <= previousIndex || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https"))
            {
                return false;
            }

            previousIndex = index;
        }

        int total = data.GetProperty("totalCitationCount").GetInt32();

        int omitted = data.GetProperty("omittedCitationCount").GetInt32();

        if (total < previousIndex || omitted != total - citations.GetArrayLength())
        {
            return false;
        }

        firstIndex = citations[0].GetProperty("index").GetInt32();

        firstUrl = citations[0].GetProperty("url").GetString()!;

        return true;
    }

    private static bool HasUniqueObjectProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);

            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || !HasUniqueObjectProperties(property.Value))
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().All(HasUniqueObjectProperties);
        }

        return true;
    }

    private static bool IsTranscriptRow(
        JsonElement row,
        Guid sessionId,
        string role,
        string? content = null,
        string? toolName = null) =>
        HasUniqueObjectProperties(row)
            && row.GetProperty("sessionId").GetGuid() == sessionId
            && row.GetProperty("role").GetString() == role
            && (!row.TryGetProperty("toolName", out JsonElement tool)
                ? toolName is null
                : tool.GetString() == toolName)
            && (!row.TryGetProperty("toolCallId", out JsonElement callId) || callId.ValueKind == JsonValueKind.Null)
            && row.GetProperty("content").ValueKind == JsonValueKind.String
            && (content is null
                || (role == "assistant"
                    ? PublishedArcanumOllamaQualificationTests.NormalizeModelAnswer(row.GetProperty("content").GetString()!)
                    : row.GetProperty("content").GetString()) == content);

    internal static string RequirePerplexityCredential(string variableName, Func<string, string?> read)
    {
        if (string.IsNullOrEmpty(variableName)
            || !(char.IsAsciiLetter(variableName[0]) || variableName[0] == '_')
            || variableName.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new InvalidOperationException("The Perplexity credential must select an environment variable name.");
        }

        string? credential = read(variableName);

        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException("Enhanced qualification requires a nonblank credential environment variable.");
        }

        return credential;
    }

    public static bool IsGitHubActions(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "1", StringComparison.Ordinal);

    public static bool IsLocalOllamaEndpoint(string? raw, out Uri? endpoint)
    {
        endpoint = null;

        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        string literalHost = parsed.Host.Trim('[', ']');

        if (parsed.Scheme != Uri.UriSchemeHttp
            || !IPAddress.TryParse(literalHost, out IPAddress? address)
            || !IPAddress.IsLoopback(address)
            || parsed.Port <= 0
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || parsed.AbsolutePath is not ("/v1" or "/v1/"))
        {
            return false;
        }

        endpoint = parsed.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? parsed
            : new Uri(parsed.AbsoluteUri + "/");
        return true;
    }

    /// <summary>
    /// The real-model qualifier may contact only the literal loopback endpoint it validated. A
    /// redirect or ambient proxy would turn that local preflight into an unreviewed network hop.
    /// </summary>
    public static SocketsHttpHandler CreateLoopbackOnlyHttpHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            Proxy = null,
            UseCookies = false,
        };

    public static async Task<byte[]?> ReadCappedResponseBytesAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (content.Headers.ContentLength is { } declaredLength
            && declaredLength > maxBytes)
        {
            return null;
        }

        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using MemoryStream output = new(Math.Min(maxBytes, 81_920));

        byte[] buffer = new byte[Math.Min(maxBytes, 81_920)];

        int total = 0;

        while (true)
        {
            int read = await stream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return output.ToArray();
            }

            if (total > maxBytes - read)
            {
                return null;
            }

            await output
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

            total += read;
        }
    }

    public static bool IsQualifiedImage(string? path, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);

            if (stream.Length is < 4 or > 20L * 1024L * 1024L)
            {
                return false;
            }

            Span<byte> prefix = stackalloc byte[3];

            if (stream.Read(prefix) != prefix.Length
                || prefix[0] != 0xFF
                || prefix[1] != 0xD8
                || prefix[2] != 0xFF)
            {
                return false;
            }

            stream.Position = 0;
            return string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)),
                expectedSha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or SecurityException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsExpectedVisionAnswer(
        string? answer,
        string conversationMarker,
        string correctedToken)
    {
        if (string.IsNullOrEmpty(answer)
            || string.IsNullOrEmpty(conversationMarker)
            || string.IsNullOrEmpty(correctedToken))
        {
            return false;
        }

        string[] fields = answer.Split("; ", StringSplitOptions.None);

        return fields.Length == 7
            && string.Equals(fields[0], $"MARKER={conversationMarker}", StringComparison.Ordinal)
            && string.Equals(fields[1], $"TOKEN={correctedToken}", StringComparison.Ordinal)
            && IsExpectedVisionField(fields[2], "OBJECT", "STOP SIGN")
            && IsExpectedVisionField(fields[3], "COLOR", "RED")
            && IsExpectedVisionField(fields[4], "SHAPE", "OCTAGON", "OCTAGONAL")
            && IsExpectedVisionField(fields[5], "SIDES", "8")
            && IsExpectedVisionField(fields[6], "TEXT", "STOP");
    }

    public static bool IsNativeBinary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            FileInfo file = new(path);

            if (!file.Exists
                || file.Length < 4
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || file.LinkTarget is not null)
            {
                return false;
            }

            using FileStream stream = File.OpenRead(path);
            Span<byte> prefix = stackalloc byte[4];

            if (stream.Read(prefix) != prefix.Length
                || (prefix[0] == (byte)'#' && prefix[1] == (byte)'!'))
            {
                return false;
            }

            return IsMachOMagic(prefix) || IsPeImage(stream, prefix);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or SecurityException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsExpectedVisionField(
        string field,
        string name,
        params ReadOnlySpan<string> expectedValues)
    {
        string prefix = name + "=";

        if (!field.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> value = field.AsSpan(prefix.Length);

        foreach (string expectedValue in expectedValues)
        {
            if (value.Equals(expectedValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsVisionAnswerLeakage(params string?[] providerVisibleValues)
    {
        foreach (string? value in providerVisibleValues)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            ReadOnlySpan<char> remaining = value.AsSpan();

            while (!remaining.IsEmpty)
            {
                int tokenStart = 0;

                while (tokenStart < remaining.Length
                    && !char.IsLetterOrDigit(remaining[tokenStart]))
                {
                    tokenStart++;
                }

                remaining = remaining[tokenStart..];

                if (remaining.IsEmpty)
                {
                    break;
                }

                int tokenLength = 0;

                while (tokenLength < remaining.Length
                    && char.IsLetterOrDigit(remaining[tokenLength]))
                {
                    tokenLength++;
                }

                ReadOnlySpan<char> token = remaining[..tokenLength];

                foreach (string forbidden in ForbiddenVisionAnswerTokens)
                {
                    if (token.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                remaining = remaining[tokenLength..];
            }
        }

        return false;
    }

    public static string Redact(
        string value,
        string providerKey,
        string? masterApiKey,
        params string?[] additionalSecrets)
    {
        ArgumentNullException.ThrowIfNull(value);

        List<string> secrets = [];

        if (!string.IsNullOrEmpty(providerKey))
        {
            secrets.Add(providerKey);
        }

        if (!string.IsNullOrEmpty(masterApiKey)
            && !string.Equals(masterApiKey, providerKey, StringComparison.Ordinal))
        {
            secrets.Add(masterApiKey);
        }

        secrets.AddRange(additionalSecrets.Where(static secret => !string.IsNullOrEmpty(secret)).Select(static secret => secret!));

        secrets = secrets.Distinct(StringComparer.Ordinal).ToList();

        const string preferredReplacement = "[redacted]";

        string replacement = secrets.Any(
            secret => preferredReplacement.Contains(secret, StringComparison.Ordinal))
                ? string.Empty
                : preferredReplacement;

        List<(int Start, int End)> occurrences = [];

        foreach (string secret in secrets)
        {
            int searchOffset = 0;

            while (value.IndexOf(secret, searchOffset, StringComparison.Ordinal) is int start && start >= 0)
            {
                occurrences.Add((start, start + secret.Length));

                // A later occurrence may begin inside this one, including self-overlaps.
                searchOffset = start + 1;
            }
        }

        occurrences.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        System.Text.StringBuilder redacted = new();

        int offset = 0;

        for (int index = 0; index < occurrences.Count; index++)
        {
            (int start, int end) = occurrences[index];

            while (index + 1 < occurrences.Count && occurrences[index + 1].Start < end)
            {
                end = Math.Max(end, occurrences[++index].End);
            }

            redacted.Append(value.AsSpan(offset, start - offset));

            redacted.Append(replacement);

            offset = end;
        }

        redacted.Append(value.AsSpan(offset));

        return redacted.ToString();
    }

    public static InvalidOperationException CreateSanitizedFailure(
        string context,
        Exception failure,
        string providerKey,
        string? masterApiKey,
        params string?[] additionalSecrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentNullException.ThrowIfNull(failure);

        string failureType = failure.GetType().FullName ?? failure.GetType().Name;
        string safeContext = Redact(context, providerKey, masterApiKey, additionalSecrets);
        string detail = Redact(failure.Message, providerKey, masterApiKey, additionalSecrets);

        return new InvalidOperationException(
            $"{safeContext} Failure type: {failureType}. {detail}");
    }

    private static bool IsMachOMagic(ReadOnlySpan<byte> prefix) =>
        BinaryPrimitives.ReadUInt32BigEndian(prefix) is
            0xFEEDFACEu
                or 0xCEFAEDFEu
                or 0xFEEDFACFu
                or 0xCFFAEDFEu
                or 0xCAFEBABEu
                or 0xBEBAFECAu
                or 0xCAFEBABFu
                or 0xBFBAFECAu;

    private static bool IsPeImage(
        FileStream stream,
        ReadOnlySpan<byte> prefix)
    {
        if (prefix[0] != (byte)'M' || prefix[1] != (byte)'Z')
        {
            return false;
        }

        stream.Position = 0x3C;
        Span<byte> offsetBytes = stackalloc byte[4];

        if (stream.Read(offsetBytes) != offsetBytes.Length)
        {
            return false;
        }

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(offsetBytes);

        if (peOffset < 0 || peOffset > stream.Length - 4)
        {
            return false;
        }

        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];

        return stream.Read(signature) == signature.Length
            && signature[0] == (byte)'P'
            && signature[1] == (byte)'E'
            && signature[2] == 0
            && signature[3] == 0;
    }

    public static void DeleteDirectoryWithRetries(string path) =>
        DeleteDirectoryWithRetries(
            path,
            Directory.Exists,
            static (directory, recursive) => Directory.Delete(directory, recursive),
            Thread.Sleep);

    internal static void DeleteDirectoryWithRetries(
        string path,
        Func<string, bool> directoryExists,
        Action<string, bool> deleteDirectory,
        Action<TimeSpan> delay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ArgumentNullException.ThrowIfNull(directoryExists);

        ArgumentNullException.ThrowIfNull(deleteDirectory);

        ArgumentNullException.ThrowIfNull(delay);

        Exception? lastFailure = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!directoryExists(path))
                {
                    return;
                }

                deleteDirectory(path, true);

                if (!directoryExists(path))
                {
                    return;
                }

                lastFailure = new IOException(
                    $"Temporary directory still exists after cleanup attempt {attempt}: {path}");
            }
            catch (IOException exception)
            {
                lastFailure = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastFailure = exception;
            }

            if (attempt < 3)
            {
                delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        throw new IOException(
            $"Could not remove local qualification temporary directory after three attempts: {path}",
            lastFailure);
    }
}
