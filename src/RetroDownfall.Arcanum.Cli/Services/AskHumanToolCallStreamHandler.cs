using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// When NDJSON streaming exposes <see cref="IntelligenceEventType.ToolCall"/> for <c>ask_human</c>, submits the human answer (or unattended stub) to the API.
/// </summary>
internal static class AskHumanToolCallStreamHandler
{
    public static async Task<bool> TryHandleAskHumanAsync(
        IntelligenceEvent evt,
        bool unattended,
        ArcanumApiClient apiClient,
        CancellationToken cancellationToken)
    {
        if (evt.Type != IntelligenceEventType.ToolCall)
        {
            return false;
        }

        if (!string.Equals(evt.Message, "ask_human", StringComparison.Ordinal))
        {
            return false;
        }

        AskHumanParams? args = ParseArgs(evt.Data);

        if (args is null)
        {
            return false;
        }

        if (unattended)
        {
            _ = await apiClient
                .SubmitHumanResponseAsync(
                    args.PromptId,
                    "System: The user is in unattended mode. Proceed using your best judgment.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            Console.Out.Flush();

            Console.Error.Flush();

            string answer = AnsiConsole.Ask<string>($"\n[bold magenta]Mage asks:[/] {Markup.Escape(args.Question)}");

            _ = await apiClient.SubmitHumanResponseAsync(args.PromptId, answer, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static AskHumanParams? ParseArgs(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        int brace = data.IndexOf('{');

        if (brace < 0)
        {
            return null;
        }

        string json = data.Substring(brace);

        try
        {
            return JsonSerializer.Deserialize(json, McpJsonSerializerContext.Default.AskHumanParams);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
