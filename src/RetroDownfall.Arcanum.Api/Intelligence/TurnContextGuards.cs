using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Helpers for per-call context preflight, tool-exchange grouping, and disconnect policy resolution.
/// </summary>
internal static class TurnContextGuards
{

    public const string IdempotencyTerminalItemKey = "Arcanum.IdempotencyTerminal";

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
        int maxTokens)
    {
        if (countTokens(messages) <= maxTokens)
        {
            return true;
        }

        int guard = messages.Count;

        while (countTokens(messages) > maxTokens && guard-- > 0)
        {
            int removeAt = -1;

            for (int i = 0; i < messages.Count - 1; i++)
            {
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

            messages.RemoveAt(removeAt + 1);
            messages.RemoveAt(removeAt);
        }

        return countTokens(messages) <= maxTokens;
    }

    public static Result CheckContextBudget(
        int messageTokens,
        IEnumerable<AITool>? tools,
        int contextWindowLimit,
        int reservedOutputTokens)
    {
        int toolSchemaTokens = EstimateToolSchemaTokens(tools);
        int reserved = Math.Max(0, reservedOutputTokens);
        long total = (long)Math.Max(0, messageTokens) + toolSchemaTokens + reserved;
        int limit = Math.Max(1, contextWindowLimit);

        if (total <= limit)
        {
            return Result.Success();
        }

        return Result.Failure(new Error(
            ErrorCodes.Hub.ContextBudgetExceeded,
            $"Context budget exceeded: ~{total} tokens (messages+tools+reserved output) exceed the {limit}-token window."));
    }

    public static int EstimateToolSchemaTokens(IEnumerable<AITool>? tools)
    {
        if (tools is null)
        {
            return 0;
        }

        long total = 0;
        int count = 0;

        foreach (AITool tool in tools)
        {
            count++;
            string name = tool.Name ?? string.Empty;
            string description = tool.Description ?? string.Empty;
            total += 8 + ((name.Length + description.Length + 64) / 4);
        }

        if (count == 0)
        {
            return 0;
        }

        return total > int.MaxValue ? int.MaxValue : (int)total;
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

}
