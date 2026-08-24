using System.Text.Json;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Cli.Services;

internal enum AskHumanResult
{

    NotHandled,

    Handled,

    SubmitFailed,

    ParseFailed,

    /// <summary>Interactive input race started; stream pump must continue and <see cref="ConsoleAskHumanCoordinator.DrainAsync"/> later.</summary>
    PendingInput,

}

/// <summary>
/// When NDJSON streaming exposes <see cref="IntelligenceEventType.ToolCall"/> for <c>ask_human</c>,
/// submits the human answer (or unattended stub) to the API.
/// Interactive callers that pump the stream should prefer <see cref="ConsoleAskHumanCoordinator"/>
/// so NDJSON continues while input is pending; this method remains a sync-friendly wrapper.
/// </summary>
internal static class AskHumanToolCallStreamHandler
{
    public static async Task<AskHumanResult> TryHandleAskHumanAsync(
        IntelligenceEvent evt,
        bool unattended,
        bool isInteractive,
        ArcanumApiClient apiClient,
        IThemePalette palette,
        CancellationToken cancellationToken)
    {
        ConsoleAskHumanCoordinator coordinator = new(apiClient, palette);
        AskHumanResult begin = await coordinator
            .TryBeginAsync(evt, unattended, isInteractive, cancellationToken)
            .ConfigureAwait(false);

        if (begin != AskHumanResult.PendingInput)
        {
            return begin;
        }

        // Legacy blocking path: wait for the interactive race (no concurrent stream pump here).
        AskHumanResult? drained = await coordinator.DrainAsync(cancellationToken).ConfigureAwait(false);
        return drained ?? AskHumanResult.Handled;
    }

    /// <summary>
    /// Prefers structured <see cref="IntelligenceToolCallEvent.ArgumentsJson"/>; falls back to
    /// raw JSON or legacy <c>ask_human: {json}</c> in <see cref="IntelligenceEvent.Data"/>.
    /// </summary>
    internal static bool TryParseAskHumanArgs(
        IntelligenceEvent evt,
        out AskHumanParams? args,
        out string? errorMessage)
    {
        args = null;

        errorMessage = null;

        string? payload = evt.ToolCall?.ArgumentsJson;

        if (string.IsNullOrWhiteSpace(payload))
        {
            payload = evt.Data;
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        string? json = ExtractJsonObject(payload);

        if (json is null)
        {
            // Non-JSON status/exception lines are ignored (not a parse failure for ask_human).
            return false;
        }

        try
        {
            args = JsonSerializer.Deserialize(json, McpJsonSerializerContext.Default.AskHumanParams);

            if (args is null
                || string.IsNullOrWhiteSpace(args.Question)
                || string.IsNullOrWhiteSpace(args.PromptId))
            {
                errorMessage = "ask_human: malformed tool arguments (question and promptId are required).";

                args = null;

                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            errorMessage = "ask_human: malformed tool arguments (invalid JSON).";

            return false;
        }
    }

    /// <summary>
    /// Accepts raw <c>{...}</c> or legacy <c>tool_name: {...}</c>. Rejects non-object payloads.
    /// </summary>
    internal static string? ExtractJsonObject(string data)
    {
        ReadOnlySpan<char> trimmed = data.AsSpan().Trim();

        if (trimmed.Length < 2)
        {
            return null;
        }

        if (trimmed[0] == '{')
        {
            return trimmed[^1] == '}' ? trimmed.ToString() : null;
        }

        // Legacy FormatToolCallEventData: "ask_human: {...}"
        int brace = trimmed.IndexOf('{');

        if (brace < 0)
        {
            return null;
        }

        ReadOnlySpan<char> candidate = trimmed[brace..];

        return candidate.Length >= 2 && candidate[^1] == '}' ? candidate.ToString() : null;
    }

}
