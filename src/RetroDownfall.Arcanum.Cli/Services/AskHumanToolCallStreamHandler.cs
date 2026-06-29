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

}

/// <summary>
/// When NDJSON streaming exposes <see cref="IntelligenceEventType.ToolCall"/> for <c>ask_human</c>, submits the human answer (or unattended stub) to the API.
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

        if (!string.Equals(evt.Message, "ask_human", StringComparison.Ordinal))
        {
            return AskHumanResult.NotHandled;
        }

        AskHumanParams? args = ParseArgs(evt.Data);

        if (args is null)
        {
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

                // W4.1: was a silent SubmitFailed — tell the operator why the prompt could not be answered.
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
            // W4.1: surface the actual error code/message instead of a generic line so a failed
            // submit (unknown/expired promptId, transport fault) is diagnosable.
            AnsiConsole.MarkupLine(
                palette.ErrorMarkup(Markup.Escape(
                    $"Failed to submit response to Daemon ({submitResult.Error.Code}): {submitResult.Error.Message}")));

            return AskHumanResult.SubmitFailed;
        }

        return AskHumanResult.Handled;
    }

    private static AskHumanParams? ParseArgs(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        ReadOnlySpan<char> trimmed = data.AsSpan().Trim();

        if (trimmed.Length < 2
            || trimmed[0] != '{'
            || trimmed[^1] != '}')
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(trimmed.ToString(), McpJsonSerializerContext.Default.AskHumanParams);
        }
        catch (JsonException)
        {
            return null;
        }
    }

}
