using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Configuration;
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
    IServiceScopeFactory scopeFactory,
    ICliEnvironment cliEnvironment,
    IOptions<ArcanumSettings> arcanumSettings) : AsyncCommand<AskCommand.Settings>
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

        if (!InferenceFlagBinder.TryParse(settings, palette, out InferenceFlagBinder.Parsed flags, out int flagsExit))
        {
            return flagsExit == 0 ? 1 : flagsExit;
        }

        List<ScryingFocusDto>? scryingFoci = null;

        if (settings.Image is { Length: > 0 } imagePaths)
        {
            long maxImageBytes = ArcanumSettingClamps.ScryingMaxImageBytes(arcanumSettings.Value.Scrying.MaxImageBytes);

            string[] allowedMimeTypes = arcanumSettings.Value.Scrying.AllowedMimeTypes ?? [];

            List<ScryingFocusDto> foci = new(imagePaths.Length);

            foreach (string imagePath in imagePaths)
            {
                string fullPath;

                try
                {
                    fullPath = Path.GetFullPath(imagePath);
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    AnsiConsole.MarkupLine(
                        palette.ErrorLabelMarkup(
                            Markup.Escape("Error:"),
                            Markup.Escape($"--image '{imagePath}' could not be resolved as a path ({ex.GetType().Name}).")));

                    return 1;
                }

                if (!File.Exists(fullPath))
                {
                    AnsiConsole.MarkupLine(
                        palette.ErrorLabelMarkup(
                            Markup.Escape("Error:"),
                            Markup.Escape($"--image '{fullPath}' not found.")));

                    return 1;
                }

                ScryingFocusStager.StagingResult staged = ScryingFocusStager.Stage(fullPath, maxImageBytes, allowedMimeTypes);

                if (staged.Error is not null)
                {
                    AnsiConsole.MarkupLine(
                        palette.ErrorLabelMarkup(
                            Markup.Escape($"--image '{Path.GetFileName(fullPath)}':"),
                            Markup.Escape(staged.Error)));

                    return 1;
                }

                foci.Add(staged.Focus!);

                AnsiConsole.MarkupLine(
                    $"{palette.HighlightMarkup(Markup.Escape("Scrying focus:"))} {palette.TextMarkup(Markup.Escape($"{Path.GetFileName(fullPath)} ({ScryingFocusStager.FormatByteCount(staged.FileSizeBytes ?? 0)})"))}");
            }

            scryingFoci = foci;
        }

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(settings.Campaign))
        {

            if (!Guid.TryParse(settings.Campaign, out Guid parsedCampaignId))
            {
                AnsiConsole.MarkupLine(
                    palette.ErrorLabelMarkup(Markup.Escape("Error:"), Markup.Escape("--campaign must be a valid GUID.")));

                return 1;
            }

            campaignId = parsedCampaignId;

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

            try
            {
                await grimoireBootstrapper.EnsureInitializedAsync(linked.Token).ConfigureAwait(false);
            }
            catch (MissingMasterApiKeyException ex)
            {

                stderrConsole.MarkupLine(
                    palette.ErrorLabelMarkup(Markup.Escape("Error:"), Markup.Escape(ex.Message)));

                return 1;

            }

            ChronosyncReport chronosyncDelta;

            await using (AsyncServiceScope chronosyncScope = scopeFactory.CreateAsyncScope())
            {
                IChronosyncEngine chronosync = chronosyncScope.ServiceProvider.GetRequiredService<IChronosyncEngine>();

                chronosyncDelta = await chronosync.AnalyzeAndSyncAsync(snapshot, linked.Token).ConfigureAwait(false);
            }

            Guid? sessionId = null;

            if (settings.New)
            {
                session.ClearSession();
            }
            else
            {
                sessionId = session.GetLastSessionId();
            }

            PingRequest ping = new(
                prompt,
                string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim(),
                cwd,
                snapshot,
                sessionId,
                UnattendedMode: settings.Unattended,
                ChronosyncDelta: chronosyncDelta,
                Temperature: flags.Temperature,
                TopP: flags.TopP,
                MaxOutputTokens: flags.MaxOutputTokens,
                Stop: flags.Stop,
                Seed: flags.Seed,
                ResponseFormat: flags.ResponseFormat,
                PresencePenalty: flags.PresencePenalty,
                FrequencyPenalty: flags.FrequencyPenalty,
                CampaignId: campaignId,
                ScryingFoci: scryingFoci);

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
                            .TryHandleAskHumanAsync(
                                evt,
                                settings.Unattended,
                                cliEnvironment.IsInteractive,
                                apiClient,
                                palette,
                                linked.Token)
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

                    case IntelligenceEventType.SessionBound:
                    case IntelligenceEventType.ConversationBound:

                        if (evt.Data is not null && Guid.TryParse(evt.Data, out Guid boundId))
                        {
                            session.SaveSessionId(boundId);
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
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // W4.1: return 130 (SIGINT) only on an actual user/host cancellation. A cancellation
            // from another source (e.g. a transient client timeout surfacing as OCE) falls through
            // to the generic handler and reports a normal error (exit 1).
            return 130;
        }
        catch (Exception ex)
        {

            stderrConsole.MarkupLine(
                palette.ErrorLabelMarkup(Markup.Escape("Error:"), Markup.Escape(ex.Message)));

            return 1;

        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        if (finalText is null)
        {

            string accumulated = accumulatedText.ToString();

            if (!string.IsNullOrEmpty(accumulated))
            {

                finalText = accumulated;

            }

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

    internal static string BuildPrompt(Settings settings, CommandContext context)
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

    public sealed class Settings : CommandSettings, IInferenceFlagInputs
    {
        [CommandArgument(0, "[PROMPT...]")]
        public string[] PromptWords { get; init; } = [];

        [CommandOption("-m|--model")]
        [Description("The specific model to use for this inference request")]
        public string? Model { get; init; }

        [CommandOption("-n|--new")]
        [Description("Start a new session thread, clearing the previous session.")]
        public bool New { get; init; }

        [CommandOption("--unattended")]
        [Description("Do not block for ask_human; auto-reply so the Mage proceeds without a live operator.")]
        public bool Unattended { get; set; }

        [CommandOption("-c|--campaign <ID>")]
        [Description("Campaign GUID to resolve the workspace from (400 Campaign.NotFound if unknown).")]
        public string? Campaign { get; init; }

        [CommandOption("--temperature <VALUE>")]
        [Description("Sampling temperature 0\u20132 (lower = more deterministic).")]
        public string? Temperature { get; init; }

        [CommandOption("--top-p <VALUE>")]
        [Description("Nucleus sampling cutoff 0\u20131.")]
        public string? TopP { get; init; }

        [CommandOption("--max-tokens <N>")]
        [Description("Maximum output tokens for this turn.")]
        public string? MaxTokens { get; init; }

        [CommandOption("--seed <N>")]
        [Description("Seed for sampling determinism (provider support varies).")]
        public string? Seed { get; init; }

        [CommandOption("--stop <SEQUENCE>")]
        [Description("Stop sequence(s); pass --stop multiple times for several stops.")]
        public string[]? Stop { get; init; }

        [CommandOption("--response-format <KIND>")]
        [Description("Response format: text | json_object | json_schema.")]
        public string? ResponseFormat { get; init; }

        [CommandOption("--presence-penalty <VALUE>")]
        [Description("Presence penalty \u22122..2 (positive discourages repetition).")]
        public string? PresencePenalty { get; init; }

        [CommandOption("--frequency-penalty <VALUE>")]
        [Description("Frequency penalty \u22122..2 (positive penalizes frequent tokens).")]
        public string? FrequencyPenalty { get; init; }

        [CommandOption("--image <PATH>")]
        [Description("Attach an image (Scrying focus) for this turn; repeatable. Requires a vision-capable model.")]
        public string[]? Image { get; init; }
    }
}
