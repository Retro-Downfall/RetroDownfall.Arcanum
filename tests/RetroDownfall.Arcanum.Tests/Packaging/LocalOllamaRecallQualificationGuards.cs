using System.Globalization;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Memory;

namespace RetroDownfall.Arcanum.Tests.Packaging;

internal static class LocalOllamaRecallQualificationGuards
{
    internal sealed record Refusal(string Code, int FrameOrdinal, IntelligenceEventType? Type = null);

    internal sealed record Evidence(string CallId, string ArgumentsJson, string ResultJson, string Answer, string? RawAnswer = null);

    internal static bool MatchesPersistedTurn(IReadOnlyList<JsonElement> before,
        IReadOnlyList<JsonElement> after, Guid sessionId, string prompt, Evidence evidence)
    {
        try
        {
            if (after.Count != before.Count + 4
                || !LocalOllamaQualificationGuards.SameDurableRows(before, after.Take(before.Count).ToArray())
                || after.Select(static row => row.GetProperty("id").GetGuid()).Distinct().Count() != after.Count)
            {
                return false;
            }

            JsonElement[] appended = after.Skip(before.Count).ToArray();

            return appended.All(row => row.GetProperty("sessionId").GetGuid() == sessionId)
                && appended.Count(row => IsRow(row, "user", prompt)) == 1
                && appended.Count(row => IsRow(row, "assistant", evidence.RawAnswer ?? evidence.Answer)) == 1
                && appended.Count(row => IsRow(row, "assistant", $"[ToolCall: recall_memory({evidence.ArgumentsJson})]")
                    && row.GetProperty("toolName").GetString() == "recall_memory") == 1
                && appended.Count(row => IsRow(row, "system", $"[ToolResult: {evidence.ResultJson}]")) == 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return false;
        }
    }

    internal static async Task<Evidence?> ReadAsync(TextReader reader, Guid sessionId, string query, string token,
        Guid version, CancellationToken cancellationToken, Action<Refusal>? onRefusal = null)
    {
        int frameOrdinal = 0;

        IntelligenceEventType? frameType = null;

        Evidence? Refuse(string code)
        {
            onRefusal?.Invoke(new(code, frameOrdinal, frameType));

            return null;
        }
        const int maximumCharacters = 1024 * 1024;

        StringBuilder text = new();

        char[] buffer = new char[4096];

        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);

                if (read == 0) break;

                if (text.Length + read > maximumCharacters) return Refuse("Stream.TooLarge");

                text.Append(buffer, 0, read);
            }

            if (text.Length == 0) return Refuse("Stream.Empty");

            if (text[^1] != '\n') return Refuse("Stream.Unterminated");

            IntelligenceToolCallEvent? call = null;

            string? resultJson = null;

            StringBuilder tokens = new();

            Evidence? terminal = null;

            foreach (string line in text.ToString().Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                frameOrdinal++;

                frameType = null;

                using var document = JsonDocument.Parse(line);

                var root = document.RootElement;

                if (!UniqueProperties(root) || root.ValueKind != JsonValueKind.Object
                    || root.GetProperty("type").ValueKind != JsonValueKind.String
                    || root.GetProperty("message").ValueKind != JsonValueKind.String)
                {
                    return Refuse("Envelope.InvalidShape");
                }

                string? type = root.GetProperty("type").GetString();

                if (type is not ("status" or "sessionBound" or "conversationBound" or "token" or "reasoning"
                    or "context" or "result" or "error" or "toolCall" or "toolResult" or "warded"
                    or "wardResolved" or "toolError" or "attachmentRefreshed"))
                {
                    return Refuse("Envelope.UnsupportedType");
                }

                IntelligenceEvent frame = JsonSerializer.Deserialize(line, ArcanumJsonContext.Default.IntelligenceEvent)!;

                frameType = frame.Type;

                if (frame.ToolCall is not null && frame.Type is not (IntelligenceEventType.ToolCall or IntelligenceEventType.ToolResult))
                {
                    return Refuse("Envelope.UnexpectedStructuredCall");
                }

                switch (frame.Type)
                {
                    case IntelligenceEventType.ToolCall:
                        if (terminal is not null || call is not null || resultJson is not null)
                            return Refuse("ToolCall.UnexpectedOrder");

                        if (frame.ToolCall is not { } proposed || string.IsNullOrWhiteSpace(proposed.CallId)
                            || proposed.Name != "recall_memory" || proposed.Index != 0 || frame.Message != proposed.Name)
                            return Refuse("ToolCall.InvalidIdentity");

                        if (frame.Data != proposed.ArgumentsJson) return Refuse("ToolCall.DisplayMismatch");

                        if (string.IsNullOrWhiteSpace(proposed.ArgumentsJson) || !RecallArguments(proposed.ArgumentsJson, query))
                            return Refuse("ToolCall.InvalidArguments");

                        call = proposed;

                        break;

                    case IntelligenceEventType.ToolResult:
                        if (terminal is not null || call is null || resultJson is not null)
                            return Refuse("ToolResult.UnexpectedOrder");

                        if (frame.ToolCall is not { } completed || completed.CallId != call.CallId
                            || completed.Name != call.Name || completed.Index != call.Index
                            || completed.ArgumentsJson != call.ArgumentsJson || frame.Message != call.Name)
                            return Refuse("ToolResult.CorrelationMismatch");

                        if (frame.Data is null || !RecallResult(frame.Data, token, version))
                            return Refuse("ToolResult.InvalidPayload");

                        resultJson = frame.Data;

                        break;

                    case IntelligenceEventType.Token:
                        if (terminal is not null) return Refuse("Token.AfterTerminal");

                        if (resultJson is null) return Refuse("Token.BeforeToolResult");

                        if (frame.Data is null || frame.Message != frame.Data) return Refuse("Token.DisplayMismatch");

                        tokens.Append(frame.Data);

                        break;

                    case IntelligenceEventType.Result:
                        if (terminal is not null) return Refuse("Terminal.Repeated");

                        if (call is null) return Refuse("Terminal.MissingToolCall");

                        if (resultJson is null) return Refuse("Terminal.MissingToolResult");

                        if (tokens.ToString() != frame.Message) return Refuse("Terminal.TokenMismatch");

                        if (frame.FinishReason != "stop") return Refuse("Terminal.InvalidFinishReason");

                        if (!long.TryParse(frame.Data, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                            return Refuse("Terminal.InvalidUsage");

                        string answer = PublishedArcanumOllamaQualificationTests.NormalizeModelAnswer(frame.Message);

                        string prefix = $"RECALLED_TOKEN={token}; VERSION=";

                        if (!answer.StartsWith(prefix, StringComparison.Ordinal)
                            || !Guid.TryParse(answer[prefix.Length..], out Guid observedVersion) || observedVersion != version)
                            return Refuse("Terminal.AnswerMismatch");

                        terminal = new(call.CallId, call.ArgumentsJson, resultJson, answer, frame.Message);

                        break;

                    case IntelligenceEventType.SessionBound:
                    case IntelligenceEventType.ConversationBound:
                        if (!Guid.TryParse(frame.Data, out Guid bound) || bound != sessionId) return Refuse("Binding.Mismatch");

                        break;

                    case IntelligenceEventType.Error:
                        return Refuse("Provider.Error");

                    case IntelligenceEventType.ToolError:
                        return Refuse("Tool.Error");
                }
            }

            return terminal ?? Refuse("Terminal.Missing");
        }
        catch (Exception exception) when (exception is JsonException or IOException or DecoderFallbackException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Refuse(exception switch
            {
                JsonException => "Envelope.MalformedJson",
                IOException => "Stream.ReadFailure",
                DecoderFallbackException => "Stream.InvalidEncoding",
                KeyNotFoundException => "Envelope.MissingField",
                FormatException => "Envelope.InvalidValue",
                _ => "Envelope.InvalidShape",
            });
        }
    }

    private static bool IsRow(JsonElement row, string role, string content) =>
        row.GetProperty("role").GetString() == role && row.GetProperty("content").GetString() == content;

    private static bool RecallArguments(string json, string query)
    {
        using var document = JsonDocument.Parse(json);

        var root = document.RootElement;

        return root.ValueKind == JsonValueKind.Object && UniqueProperties(root)
            && root.EnumerateObject().All(static property => property.Name is "query" or "scope" or "limit" or "cursor")
            && (!root.TryGetProperty("cursor", out JsonElement cursor) || cursor.ValueKind == JsonValueKind.Null)
            && root.GetProperty("query").GetString() == query
            && root.GetProperty("scope").GetString() == "Covenant"
            && root.GetProperty("limit").TryGetInt32(out int limit) && limit == 1;
    }

    private static bool RecallResult(string json, string token, Guid version)
    {
        using var document = JsonDocument.Parse(json);

        if (!UniqueProperties(document.RootElement)) return false;

        MemoryRecallPage? page = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.MemoryRecallPage);

        return page is { Items.Count: 1, NextCursor: null, CandidateLimitReached: false }
            && page.Items[0] is { Source: MemoryRecallScope.Covenant } item
            && item.VersionId == version && item.Id == version.ToString("D")
            && item.Content is { } content && content.Contains(token, StringComparison.Ordinal);
    }

    private static bool UniqueProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(static property => property.Name).Distinct(StringComparer.Ordinal).Count()
            == element.EnumerateObject().Count() && element.EnumerateObject().All(static property => UniqueProperties(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(UniqueProperties),
        _ => true,
    };
}
