using System.Text;
using ConsoleAppFramework;
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

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class AskCommand(
    IEyeOfTheWorld eye,
    ArcanumApiClient apiClient,
    IThemePalette palette,
    CliSessionManager session,
    IGrimoireCliInitialization grimoireBootstrapper,
    IServiceScopeFactory scopeFactory,
    ICliEnvironment cliEnvironment,
    IOptions<ArcanumSettings> arcanumSettings)
{

    /// <summary>
    /// Ask the Mage (multi-word prompt: all words after ask, or after --; multi-turn via cli-session; --new for a fresh thread).
    /// </summary>
    /// <param name="model">-m, The specific model to use for this inference request.</param>
    /// <param name="new">-n, Start a new session thread, clearing the previous session.</param>
    /// <param name="unattended">Do not block for ask_human; auto-reply so the Mage proceeds without a live operator.</param>
    /// <param name="campaign">-c, Campaign GUID to resolve the workspace from (400 Campaign.NotFound if unknown).</param>
    /// <param name="temperature">Sampling temperature 0-2 (lower = more deterministic).</param>
    /// <param name="topP">--top-p, Nucleus sampling cutoff 0-1.</param>
    /// <param name="maxTokens">Maximum output tokens for this turn.</param>
    /// <param name="seed">Seed for sampling determinism (provider support varies).</param>
    /// <param name="stop">Stop sequence(s); pass --stop multiple times for several stops.</param>
    /// <param name="responseFormat">Response format: text | json_object | json_schema.</param>
    /// <param name="presencePenalty">Presence penalty -2..2 (positive discourages repetition).</param>
    /// <param name="frequencyPenalty">Frequency penalty -2..2 (positive penalizes frequent tokens).</param>
    /// <param name="image">Attach an image (Scrying focus) for this turn; repeatable. Requires a vision-capable model.</param>
    /// <param name="prompt">The prompt text: all words after ask, or after --.</param>
    [Command("")]
    public async Task<int> Ask(
        ConsoleAppContext context,
        CancellationToken cancellationToken,
        string? model = null,
        bool @new = false,
        bool unattended = false,
        string? campaign = null,
        string? temperature = null,
        string? topP = null,
        string? maxTokens = null,
        string? seed = null,
        string[]? stop = null,
        string? responseFormat = null,
        string? presencePenalty = null,
        string? frequencyPenalty = null,
        string[]? image = null,
        [Argument] params string[] prompt)
    {
        string promptText = BuildPrompt(prompt, context.EscapedArguments.ToArray());

        if (string.IsNullOrWhiteSpace(promptText))
        {
            AnsiConsole.MarkupLine(
                palette.ErrorLabelMarkup(
                    Markup.Escape("Error:"),
                    Markup.Escape(
                        "Prompt is required. Examples: arcanum ask What time is it? or arcanum ask -- local time")));

            return 1;
        }

        InferenceFlagInputs flagInputs = new(temperature, topP, maxTokens, seed, stop, responseFormat, presencePenalty, frequencyPenalty);

        if (!InferenceFlagBinder.TryParse(flagInputs, palette, out InferenceFlagBinder.Parsed flags, out int flagsExit))
        {
            return flagsExit == 0 ? 1 : flagsExit;
        }

        List<ScryingFocusDto>? scryingFoci = null;

        if (image is { Length: > 0 } imagePaths)
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

        if (!string.IsNullOrWhiteSpace(campaign))
        {

            if (!Guid.TryParse(campaign, out Guid parsedCampaignId))
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

            if (@new)
            {
                session.ClearSession();
            }
            else
            {
                sessionId = session.GetLastSessionId();
            }

            PingRequest ping = new(
                promptText,
                string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                cwd,
                snapshot,
                sessionId,
                UnattendedMode: unattended,
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
                                unattended,
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

                    case IntelligenceEventType.ToolError:

                        stderrConsole.MarkupLine(palette.ErrorMarkup(Markup.Escape($"⚠ Tool {evt.Message} failed (tolerated)")));

                        break;

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

    internal static string BuildPrompt(string[] promptWords, string[]? escapedArguments)
    {
        List<string> parts = new(promptWords.Length + 8);

        foreach (string word in promptWords)
        {
            if (!string.IsNullOrWhiteSpace(word))
            {
                parts.Add(word.Trim());
            }
        }

        if (escapedArguments is { } escaped)
        {
            foreach (string token in escaped)
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    parts.Add(token.Trim());
                }
            }
        }

        return string.Join(' ', parts);
    }

}
