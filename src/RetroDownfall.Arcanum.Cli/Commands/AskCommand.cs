using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class AskCommand(
    IEyeOfTheWorld eye,
    ArcanumApiClient apiClient,
    IThemePalette palette,
    CliSessionManager session,
    IGrimoireCliInitialization grimoireBootstrapper,
    IServiceScopeFactory scopeFactory) : AsyncCommand<AskCommand.Settings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        string prompt = BuildPrompt(settings, context);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            AnsiConsole.MarkupLine(
                palette.ErrorLabelMarkup(
                    Markup.Escape("Error:"),
                    Markup.Escape(
                        "Prompt is required. Examples: arcanum ask What time is it? or arcanum ask -- local time")));

            return 1;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;

            try
            {
                linked.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        Console.CancelKeyPress += OnCancelKeyPress;

        IAnsiConsole stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        bool streamedTokens = false;

        string? finalText = null;

        var accumulatedText = new StringBuilder();

        try
        {
            string cwd = Environment.CurrentDirectory;

            PatternSnapshot snapshot = await eye
                .PerceivePatternAsync(cwd, linked.Token)
                .ConfigureAwait(false);

            await grimoireBootstrapper.EnsureInitializedAsync(linked.Token).ConfigureAwait(false);

            ChronosyncReport chronosyncDelta;

            await using (AsyncServiceScope chronosyncScope = scopeFactory.CreateAsyncScope())
            {
                IChronosyncEngine chronosync = chronosyncScope.ServiceProvider.GetRequiredService<IChronosyncEngine>();

                chronosyncDelta = await chronosync.AnalyzeAndSyncAsync(snapshot).ConfigureAwait(false);
            }

            Guid? conversationId = null;

            if (settings.New)
            {
                session.ClearSession();
            }
            else
            {
                conversationId = session.GetLastConversationId();
            }

            PingRequest ping = new(
                prompt,
                string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim(),
                cwd,
                snapshot,
                conversationId,
                UnattendedMode: settings.Unattended,
                ChronosyncDelta: chronosyncDelta);

            await foreach (IntelligenceEvent evt in apiClient.AskStreamAsync(ping, linked.Token).ConfigureAwait(false))
            {
                switch (evt.Type)
                {
                    case IntelligenceEventType.Status:

                        stderrConsole.MarkupLine(palette.MutedMarkup(Markup.Escape(evt.Message)));

                        break;

                    case IntelligenceEventType.Token:

                        streamedTokens = true;

                        string chunk = evt.Data ?? string.Empty;

                        _ = accumulatedText.Append(chunk);

                        AnsiConsole.Write(chunk);

                        break;

                    case IntelligenceEventType.ToolCall:

                        AskHumanResult humanResult = await AskHumanToolCallStreamHandler
                            .TryHandleAskHumanAsync(evt, settings.Unattended, apiClient, palette, linked.Token)
                            .ConfigureAwait(false);

                        if (humanResult == AskHumanResult.SubmitFailed)
                        {
                            return 1;
                        }

                        if (humanResult == AskHumanResult.Handled)
                        {
                            break;
                        }

                        goto case IntelligenceEventType.ToolResult;

                    case IntelligenceEventType.ToolResult:

                        stderrConsole.MarkupLine(palette.MutedMarkup(Markup.Escape(evt.Data ?? evt.Message)));

                        break;

                    case IntelligenceEventType.ConversationBound:

                        if (evt.Data is not null && Guid.TryParse(evt.Data, out Guid boundId))
                        {
                            session.SaveConversationId(boundId);
                        }

                        break;

                    case IntelligenceEventType.Result:

                        finalText = accumulatedText.ToString();

                        break;

                    case IntelligenceEventType.Error:

                        stderrConsole.MarkupLine(
                            palette.ErrorLabelMarkup(Markup.Escape("Error:"), Markup.Escape(evt.Message)));

                        return 1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        if (finalText is null)
        {
            stderrConsole.MarkupLine(
                palette.ErrorLabelMarkup(Markup.Escape("Error:"), Markup.Escape("Stream ended without a result.")));

            return 1;
        }

        if (!streamedTokens)
        {
            await Console.Out.WriteLineAsync(finalText).ConfigureAwait(false);
        }
        else
        {
            await Console.Out.WriteAsync(Environment.NewLine).ConfigureAwait(false);
        }

        return 0;
    }

    private static string BuildPrompt(Settings settings, CommandContext context)
    {
        List<string> parts = new(settings.PromptWords.Length + 8);

        foreach (string word in settings.PromptWords)
        {
            if (!string.IsNullOrWhiteSpace(word))
            {
                parts.Add(word.Trim());
            }
        }

        if (context.Remaining.Raw is { } raw)
        {
            foreach (string token in raw)
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    parts.Add(token.Trim());
                }
            }
        }

        return string.Join(' ', parts);
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[PROMPT...]")]
        public string[] PromptWords { get; init; } = [];

        [CommandOption("-m|--model")]
        [Description("The specific model to use for this inference request")]
        public string? Model { get; init; }

        [CommandOption("-n|--new")]
        [Description("Start a new conversation thread, clearing the previous session.")]
        public bool New { get; init; }

        [CommandOption("--unattended")]
        [Description("Do not block for ask_human; auto-reply so the Mage proceeds without a live operator.")]
        public bool Unattended { get; set; }
    }
}
