using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Helpers for per-call context preflight, tool-exchange grouping, and disconnect policy resolution.
/// </summary>
internal static class TurnContextGuards
{
    private static readonly ConditionalWeakTable<MeAiChatMessage, HistoricalToolExchange> HistoricalExchanges = new();

    // Legacy tool writes have no call id on their result. The dedicated persistence metadata,
    // append positions and exact paired representation establish a group, never marker text alone.
    internal static bool IsPersistedToolExchange(Entry call, Entry result, Guid sessionId)
    {
        if (sessionId == Guid.Empty || call.SessionId != sessionId || result.SessionId != sessionId
            || call.Id == Guid.Empty || result.Id == Guid.Empty || call.Id == result.Id
            || call.Sequence <= 0 || call.Sequence == long.MaxValue || result.Sequence != call.Sequence + 1
            || call.CreatedAt != result.CreatedAt || call.ModelUsed != result.ModelUsed
            || call.Role != MessageRole.Assistant || result.Role != MessageRole.System
            || string.IsNullOrWhiteSpace(call.ToolName) || call.ToolArguments is null
            || result.ToolName is not null || result.ToolArguments is not null || result.ToolCallId is not null
            || !string.Equals(call.Content, $"[ToolCall: {call.ToolName}({call.ToolArguments})]", StringComparison.Ordinal)
            || !result.Content.StartsWith("[ToolResult: ", StringComparison.Ordinal)
            || !result.Content.EndsWith(']'))
        {
            return false;
        }

        // The normal writer uses an empty string for a call with no arguments. Nonempty
        // arguments must retain the object serialized by that writer; they are never executed.
        if (call.ToolArguments.Length == 0) return true;

        try
        {
            using JsonDocument arguments = JsonDocument.Parse(call.ToolArguments);

            return arguments.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static void RegisterHistoricalToolExchange(MeAiChatMessage call, MeAiChatMessage result) =>
        HistoricalExchanges.Add(call, new HistoricalToolExchange(result, call.Text, result.Text));

    internal static int HistoricalToolCallCount(MeAiChatMessage call) =>
        HistoricalExchanges.TryGetValue(call, out HistoricalToolExchange? exchange)
            && exchange.Matches(call, exchange.Result) ? 1 : 0;

    private static bool IsHistoricalToolExchange(MeAiChatMessage call, MeAiChatMessage result) =>
        HistoricalExchanges.TryGetValue(call, out HistoricalToolExchange? exchange)
            && ReferenceEquals(exchange.Result, result) && exchange.Matches(call, result);

    // No provider property or executable function content carries this witness. Its lifetime is
    // tied to the mapped call instance; removed payloads are not held by a process-wide transcript.
    private sealed record HistoricalToolExchange(MeAiChatMessage Result, string CallText, string ResultText)
    {
        internal bool Matches(MeAiChatMessage call, MeAiChatMessage result) =>
            call.Role == ChatRole.Assistant && result.Role == ChatRole.System
            && call.Contents.Count == 1 && call.Contents[0] is TextContent
            && result.Contents.Count == 1 && result.Contents[0] is TextContent
            && string.Equals(call.Text, CallText, StringComparison.Ordinal)
            && string.Equals(result.Text, ResultText, StringComparison.Ordinal);
    }

    public const string IdempotencyTerminalItemKey = "Arcanum.IdempotencyTerminal";

    /// <summary>
    /// A response that must never be cached as a replayable claim, independent of
    /// <see cref="IdempotencyTerminalItemKey"/> or anything <c>PersistClaimAsync</c> can infer from
    /// the buffered bytes themselves. Needed because that inference has its own fallback — a
    /// non-empty, non-aborted buffered body is treated as terminal even when nothing marked it so —
    /// so a writer that already knows its own body is a failure frame (a stream that ended in a
    /// provider-reported <c>Error</c> event, or one aborted by a thrown exception) has to say so
    /// explicitly rather than rely on simply omitting the terminal marker.
    /// </summary>
    public const string IdempotencyNeverCacheItemKey = "Arcanum.IdempotencyNeverCache";

    public static bool IsToolCallEntry(Entry entry) =>
        entry.Role == MessageRole.Assistant
        && (!string.IsNullOrEmpty(entry.ToolName)
            || (entry.Content?.StartsWith("[ToolCall:", StringComparison.Ordinal) ?? false));

    public static bool IsToolResultEntry(Entry entry) =>
        entry.Role == MessageRole.System
        && (entry.Content?.StartsWith("[ToolResult:", StringComparison.Ordinal) ?? false);

    /// <summary>
    /// Expands a deletion candidate set so ToolCall/ToolResult pairs are never split.
    /// </summary>
    public static HashSet<Guid> ExpandDeletionToCompleteToolGroups(
        IReadOnlyList<Entry> chronological,
        IEnumerable<Guid> candidates)
    {
        HashSet<Guid> ids = [.. candidates];
        Dictionary<Guid, int> indexById = new(chronological.Count);

        for (int i = 0; i < chronological.Count; i++)
        {
            indexById[chronological[i].Id] = i;
        }

        foreach (Guid id in candidates.ToArray())
        {
            if (!indexById.TryGetValue(id, out int index))
            {
                continue;
            }

            Entry entry = chronological[index];

            if (IsToolCallEntry(entry) && index + 1 < chronological.Count && IsToolResultEntry(chronological[index + 1]))
            {
                _ = ids.Add(chronological[index + 1].Id);
            }
            else if (IsToolResultEntry(entry) && index > 0 && IsToolCallEntry(chronological[index - 1]))
            {
                _ = ids.Add(chronological[index - 1].Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Drops orphan tool halves after a pin/watermark filter so history never contains a lone call or result.
    /// </summary>
    public static List<Entry> DropOrphanToolHalves(List<Entry> ordered)
    {
        if (ordered.Count == 0)
        {
            return ordered;
        }

        List<Entry> cleaned = new(ordered.Count);

        for (int i = 0; i < ordered.Count; i++)
        {
            Entry entry = ordered[i];

            if (IsToolCallEntry(entry))
            {
                if (i + 1 < ordered.Count && IsToolResultEntry(ordered[i + 1]))
                {
                    cleaned.Add(entry);
                    cleaned.Add(ordered[i + 1]);
                    i++;
                }

                continue;
            }

            if (IsToolResultEntry(entry))
            {
                continue;
            }

            cleaned.Add(entry);
        }

        return cleaned;
    }

    /// <summary>
    /// Removes oldest complete tool-exchange pairs from an in-memory chat list until under budget.
    /// Preserves leading system messages. Returns false when still over budget.
    /// </summary>
    public static bool TryTrimOldestToolExchanges(
        List<MeAiChatMessage> messages,
        Func<IReadOnlyList<MeAiChatMessage>, int> countTokens,
        int maxTokens,
        ToolLoopProgressDetector? progress = null)
    {
        int currentTokens = countTokens(messages);

        if (currentTokens <= maxTokens)
        {
            return true;
        }

        int guard = messages.Count;

        // Track the running total and subtract each removed run's own marginal
        // contribution instead of re-estimating the whole, still-largely-untrimmed transcript on
        // every iteration — an N-pair trim then costs N small estimates rather than N
        // full-transcript ones, so it no longer scales with the size of the conversation being
        // trimmed. countTokens(slice) also carries the estimator's per-call fixed overhead
        // (reserved answer/reasoning tokens, the tool schema, provider framing) — the same
        // overhead already counted once in currentTokens above — so that fixed amount is measured
        // once against an empty message list and subtracted back out of every slice estimate
        // before it is applied to the running total.
        int fixedOverhead = countTokens([]);

        // A notice is fixed host text. Estimate its own framed slice, subtracting the fixed
        // call overhead already counted above, rather than rescanning the whole transcript.
        // Framing it separately is conservative when it was appended to an existing user message;
        // the final full estimate below is authoritative for actual admission and ceiling margins.
        if (progress?.EnsureTrimNotice(messages) is { } notice)
            currentTokens += countTokens([notice]) - fixedOverhead;

        while (currentTokens > maxTokens && guard-- > 0)
        {
            int removeAt = -1;

            bool historical = false;

            for (int i = 0; i < messages.Count - 1; i++)
            {
                if (IsHistoricalToolExchange(messages[i], messages[i + 1]))
                {
                    removeAt = i;

                    historical = true;

                    break;
                }

                if (messages[i].Role == ChatRole.System)
                {
                    continue;
                }

                bool isAssistantTool = messages[i].Role == ChatRole.Assistant
                    && messages[i].Contents.Any(static c => c is FunctionCallContent);

                bool nextIsToolResult = messages[i + 1].Role == ChatRole.Tool
                    || messages[i + 1].Contents.Any(static c => c is FunctionResultContent);

                if (isAssistantTool && nextIsToolResult)
                {
                    removeAt = i;
                    break;
                }
            }

            if (removeAt < 0)
            {
                break;
            }

            // A stateless transcript maps N parallel tool calls to ONE assistant message followed by
            // N tool messages, so removing a fixed pair would split the turn and leave orphan tool
            // results that every OpenAI-compatible provider rejects. Take the whole contiguous run.
            int removeCount = historical ? 2 : 1;

            while (!historical && removeAt + removeCount < messages.Count
                && (messages[removeAt + removeCount].Role == ChatRole.Tool
                    || messages[removeAt + removeCount].Contents.Any(static c => c is FunctionResultContent)))
            {
                removeCount++;
            }

            List<MeAiChatMessage> removed = messages.GetRange(removeAt, removeCount);

            currentTokens -= countTokens(removed) - fixedOverhead;

            progress?.ObserveTrimmedExchanges(removed);

            messages.RemoveRange(removeAt, removeCount);
        }

        // Measured, not accumulated. currentTokens is a running approximation - the estimator
        // applies its safety margin as a ceiling over the whole input, and a ceiling does not
        // distribute over addition, so the removed runs' own margins never sum to exactly the share
        // they contributed - and a verdict answered from it can disagree with the authoritative
        // breakdown the caller takes on the very next line. One more estimate settles it against
        // the list that was actually left, and it is one, not one per removed run.
        return countTokens(messages) <= maxTokens;
    }

    public static Result CheckContextBudget(
        ContextTokenBreakdown breakdown,
        int contextWindowLimit)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        int limit = ArcanumSettingClamps.ContextWindowLimit(contextWindowLimit);

        if (breakdown.TotalTokens <= limit)
        {
            return Result.Success();
        }

        ContextOverflowDetails overflow = new(breakdown.Provider, breakdown.Model,
            breakdown.InputTokens, breakdown.ReservedTokens, breakdown.TotalTokens, limit);

        return Result.Failure(new Error(ErrorCodes.Hub.ContextBudgetExceeded, overflow.ToPublicMessage())
        {
            ContextOverflow = overflow,
        });
    }

    public static bool ResolveContinueThenReplay(HttpContext httpContext, DisconnectPolicy policy)
    {
        bool hasIdempotencyKey = httpContext.Request.Headers.ContainsKey(ArcanumApiHeaders.IdempotencyKey);

        return policy switch
        {
            DisconnectPolicy.ContinueThenReplay => true,
            DisconnectPolicy.CancelAbandoned => false,
            _ => hasIdempotencyKey,
        };
    }

    public static void MarkIdempotencyTerminal(HttpContext httpContext) =>
        httpContext.Items[IdempotencyTerminalItemKey] = true;

    public static bool IsIdempotencyTerminal(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(IdempotencyTerminalItemKey, out object? value) && value is true;

    public static void MarkIdempotencyNeverCache(HttpContext httpContext) =>
        httpContext.Items[IdempotencyNeverCacheItemKey] = true;

    public static bool IsIdempotencyNeverCache(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(IdempotencyNeverCacheItemKey, out object? value) && value is true;
}
