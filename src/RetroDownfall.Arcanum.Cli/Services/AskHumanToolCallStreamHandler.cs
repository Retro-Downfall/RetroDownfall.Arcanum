using System.Text.Json;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Services;

internal enum AskHumanResult
{

    NotHandled,

    Handled,

    SubmitFailed,

    ParseFailed,

}

/// <summary>
/// When NDJSON streaming exposes <see cref="IntelligenceEventType.ToolCall"/> for <c>ask_human</c>,
/// submits the human answer (or unattended stub) to the API.
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
        if (evt.Type != IntelligenceEventType.ToolCall)
        {
            return AskHumanResult.NotHandled;
        }

        string toolName = evt.ToolCall?.Name ?? evt.Message;

        if (!string.Equals(toolName, "ask_human", StringComparison.Ordinal))
        {
            return AskHumanResult.NotHandled;
        }

        if (!TryParseAskHumanArgs(evt, out AskHumanParams? args, out string? parseError) || args is null)
        {
            if (parseError is not null)
            {
                AnsiConsole.MarkupLine(
                    palette.ErrorMarkup(Markup.Escape(parseError)));

                return AskHumanResult.ParseFailed;
            }

            return AskHumanResult.NotHandled;
        }

        Result<bool> submitResult;

        if (unattended || !isInteractive)
        {
            string autoReply = unattended
                ? "System: The user is in unattended mode. Proceed using your best judgment."
                : "System: No interactive terminal is available. Proceed using your best judgment.";

            submitResult = await apiClient
                .SubmitHumanResponseAsync(args.PromptId, autoReply, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            Console.Out.Flush();

            Console.Error.Flush();

            string answer;

            try
            {

                answer = CliLineReader.ReadLine(
                    $"\n{palette.HeadingBoldMarkup(Markup.Escape("Mage asks:"))} {Markup.Escape(args.Question)} ",
                    allowEmpty: false)
                    ?? string.Empty;

            }
            catch (InvalidOperationException)
            {

                AnsiConsole.MarkupLine(
                    palette.ErrorMarkup(Markup.Escape("ask_human: no interactive input is available to answer the prompt.")));

                return AskHumanResult.SubmitFailed;

            }

            if (string.IsNullOrWhiteSpace(answer))
            {

                AnsiConsole.MarkupLine(
                    palette.ErrorMarkup(Markup.Escape("ask_human: no answer was provided; the prompt was left unanswered.")));

                return AskHumanResult.SubmitFailed;

            }

            submitResult = await apiClient
                .SubmitHumanResponseAsync(args.PromptId, answer, cancellationToken)
                .ConfigureAwait(false);
        }

        if (submitResult.IsFailure)
        {
            AnsiConsole.MarkupLine(
                palette.ErrorMarkup(Markup.Escape(
                    $"Failed to submit response to Daemon ({submitResult.Error.Code}): {submitResult.Error.Message}")));

            return AskHumanResult.SubmitFailed;
        }

        return AskHumanResult.Handled;
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
