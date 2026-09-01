using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class AskCommand(
    IEyeOfTheWorld eye,
    ArcanumApiClient apiClient,
    IThemePalette palette,
    CliSessionManager session,
    ICliEnvironment cliEnvironment,
    IOptions<ArcanumSettings> arcanumSettings,
    IArcanumServeLauncher serveLauncher,
    ICliInferenceContextResolver contextResolver,
    IConsoleDispatcher dispatcher)
{

    /// <summary>
    /// Ask the Mage (multi-word prompt: all words after ask, or after --; multi-turn via cli-session; --new for a fresh thread).
    /// </summary>
    /// <param name="model">-m, The specific model to use for this inference request.</param>
    /// <param name="new">-n, Start a new session thread, clearing the previous session.</param>
    /// <param name="unattended">Force unattended for this run (also true when <c>Arcanum:Security:Ward:UnattendedMode</c> is set). Omits <c>ask_human</c>; Ward records remain informational.</param>
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
    /// <param name="attachment">Bound session attachment GUID to reference; repeatable.</param>
    /// <param name="preparedContext">Already-resolved effective context for an internal composed CLI route.</param>
    /// <param name="prompt">The prompt text: all words after ask, or after --.</param>
    public async Task<int> Ask(
        CancellationToken cancellationToken,
        string? model = null,
        bool @new = false,
        bool unattended = false,
        string? campaign = null,
        string? workspace = null,
        string? sessionIdOption = null,
        string? temperature = null,
        string? topP = null,
        string? maxTokens = null,
        string? seed = null,
        string[]? stop = null,
        string? responseFormat = null,
        string? presencePenalty = null,
        string? frequencyPenalty = null,
        string[]? image = null,
        string[]? attachment = null,
        IReadOnlyList<AttachedFileDto>? attachedFiles = null,
        IReadOnlyList<ScryingFocusDto>? preparedScryingFoci = null,
        string? overrideSpellName = null,
        CliEffectiveContext? preparedContext = null,
        params string[] prompt)
    {
        string promptText = BuildPrompt(prompt);

        if (string.IsNullOrWhiteSpace(promptText))
        {
            CliErrorOutput.WriteMarkupLine(
                palette.ErrorLabelMarkup(
                    Markup.Escape("Error:"),
                    Markup.Escape(
                        "Prompt is required. Examples: arcanum ask What time is it? or arcanum ask -- local time")));

            return 1;
        }

        if (!AttachmentReferenceInput.TryParse(
                attachment,
                out List<Guid>? attachmentReferences,
                out string? attachmentError))
        {

            CliErrorOutput.WriteMarkupLine(
                palette.ErrorMarkup(
                    Markup.Escape(attachmentError!)));

            return 1;

        }

        InferenceFlagInputs flagInputs = new(temperature, topP, maxTokens, seed, stop, responseFormat, presencePenalty, frequencyPenalty);

        if (!InferenceFlagBinder.TryParse(flagInputs, palette, out InferenceFlagBinder.Parsed flags, out int flagsExit))
        {
            return flagsExit == 0 ? 1 : flagsExit;
        }

        List<ScryingFocusDto>? scryingFoci =
            preparedScryingFoci is { Count: > 0 }
                ? [.. preparedScryingFoci]
                : null;

        if (image is { Length: > 0 } imagePaths)
        {
            long maxImageBytes = ArcanumSettingClamps.ScryingMaxImageBytes(
                arcanumSettings.Value.ResolveScrying().MaxImageBytes);

            string[] allowedMimeTypes =
                arcanumSettings.Value.Security.AllowedImageMimeTypes ?? [];

            List<ScryingFocusDto> foci =
                scryingFoci is null
                    ? new(imagePaths.Length)
                    : new(scryingFoci);

            foreach (string imagePath in imagePaths)
            {
                string fullPath;

                try
                {
                    fullPath = Path.GetFullPath(imagePath);
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    CliErrorOutput.WriteMarkupLine(
                        palette.ErrorLabelMarkup(
                            Markup.Escape("Error:"),
                            Markup.Escape($"--image '{imagePath}' could not be resolved as a path ({ex.GetType().Name}).")));

                    return 1;
                }

                if (!File.Exists(fullPath))
                {
                    CliErrorOutput.WriteMarkupLine(
                        palette.ErrorLabelMarkup(
                            Markup.Escape("Error:"),
                            Markup.Escape($"--image '{fullPath}' not found.")));

                    return 1;
                }

                ScryingFocusStager.StagingResult staged = ScryingFocusStager.Stage(fullPath, maxImageBytes, allowedMimeTypes);

                if (staged.Error is not null)
                {
                    CliErrorOutput.WriteMarkupLine(
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

        if (@new && !string.IsNullOrWhiteSpace(sessionIdOption))
        {
            CliErrorOutput.WriteMarkupLine(
                palette.ErrorMarkup(
                    Markup.Escape("--new and --session cannot be used together.")));

            return 1;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void RequestCancellation()
        {
            try
            {
                linked.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;

            RequestCancellation();
        }

        Console.CancelKeyPress += OnCancelKeyPress;

        IAnsiConsole stderrConsole = CreateStderrConsole(
            Console.Error,
            cliEnvironment.ColorEnabled,
            cliEnvironment.IsInteractive);

        bool streamedTokens = false;

        bool receivedAnyStreamEvent = false;

        string? finalText = null;

        CliStreamContent streamContent = new();

        ConsoleAskHumanCoordinator? hitl = null;

        try
        {
            _ = await serveLauncher.EnsureRunningAsync(linked.Token).ConfigureAwait(false);

            if (@new)
            {

                if (!(await session
                        .ClearSessionAsync(
                            cancellationToken: linked.Token)
                        .ConfigureAwait(false))
                    .IsCompleted)
                {

                    return 1;

                }

            }

            string invocationDirectory = Environment.CurrentDirectory;

            CliEffectiveContext effectiveContext;

            if (preparedContext is not null)
            {

                effectiveContext = preparedContext;

            }
            else
            {

                CliInferenceContextResult contextResult = await contextResolver
                    .ResolveAsync(
                        new CliInferenceContextRequest(
                            campaign,
                            workspace,
                            model,
                            sessionIdOption,
                            invocationDirectory,
                            CliInvocationContext.Current.NoContext,
                            @new),
                        linked.Token)
                    .ConfigureAwait(false);

                if (!contextResult.IsSuccess)
                {

                    if (contextResult.IsCancelled)
                    {

                        return 0;

                    }

                    stderrConsole.MarkupLine(
                        palette.ErrorMarkup(
                            Markup.Escape(contextResult.Error ?? "CLI context could not be resolved.")));

                    return 1;

                }

                effectiveContext = contextResult.Context!;

                foreach (string warning in contextResult.Warnings)
                {

                    stderrConsole.MarkupLine(
                        palette.ErrorMarkup(Markup.Escape("Warning: " + warning)));

                }

            }

            string cwd = effectiveContext.Workspace.Value
                ?? invocationDirectory;

            Guid? campaignId = effectiveContext.Campaign.Value;

            Guid? sessionId = effectiveContext.Session.Value;

            model = effectiveContext.Model.Value;

            if (cliEnvironment.IsInteractive)
            {
                stderrConsole.MarkupLine(
                    palette.MutedMarkup(
                        Markup.Escape(
                            $"Context: campaign {campaignId?.ToString("D") ?? "server default"}; workspace {cwd}; model {model ?? "server default"}; session {sessionId?.ToString("D") ?? "new"}.")));
            }

            PatternSnapshot snapshot = await eye
                .PerceivePatternAsync(cwd, linked.Token)
                .ConfigureAwait(false);

            Result<ChronosyncReport> synchronized = await apiClient
                .SynchronizePatternAsync(snapshot, linked.Token)
                .ConfigureAwait(false);

            if (synchronized.IsFailure)
            {

                stderrConsole.MarkupLine(
                    palette.ErrorLabelMarkup(
                        Markup.Escape("Error:"),
                        Markup.Escape(synchronized.Error.Message)));

                return 1;

            }

            ChronosyncReport chronosyncDelta = synchronized.Value;

            bool effectiveUnattended = OperatorFacingUnattendedMode.Resolve(
                unattended,
                arcanumSettings.Value.Security?.Ward);

            PingRequest ping = new(
                promptText,
                string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                cwd,
                snapshot,
                sessionId,
                UnattendedMode: effectiveUnattended,
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
                AttachedFiles: attachedFiles is null
                    ? null
                    : [.. attachedFiles],
                ScryingFoci: scryingFoci,
                AttachmentReferences: attachmentReferences,
                OverrideSpellName: overrideSpellName);

            await foreach (IntelligenceEvent evt in apiClient.AskStreamAsync(ping, linked.Token).ConfigureAwait(false))
            {
                receivedAnyStreamEvent = true;
                // An ask_human prompt captures Ctrl+C as a keystroke, so CancelKeyPress never runs for
                // it. Without this hand-back the interrupt would settle the prompt and leave the pump
                // below waiting for a turn the operator has already abandoned.
                hitl ??= new ConsoleAskHumanCoordinator(
                    apiClient,
                    palette,
                    onOperatorInterrupt: RequestCancellation);
                hitl.ObserveStreamEvent(evt);

                switch (evt.Type)
                {
                    case IntelligenceEventType.Status:

                        CliStreamDiagnostic.WriteMarkupLine(
                            stderrConsole,
                            streamContent,
                            palette.MutedMarkup(Markup.Escape(evt.Message)));

                        break;

                    case IntelligenceEventType.Token:

                        streamedTokens = true;

                        string chunk = evt.Data ?? string.Empty;

                        _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, palette);
                        streamContent.AppendAnswer(chunk);

                        // Raw model text is payload, not presentation: Spectre renders a string as a
                        // Text renderable and hard-wraps it at Profile.Width (80 when stdout is
                        // redirected), which would inject newlines the model never produced.
                        Console.Out.Write(chunk);

                        break;

                    case IntelligenceEventType.Reasoning:

                        _ = streamContent.AppendReasoning(evt);

                        break;

                    case IntelligenceEventType.ToolCall:

                        AskHumanResult humanResult = await hitl
                            .TryBeginAsync(
                                evt,
                                effectiveUnattended,
                                cliEnvironment.IsInteractive,
                                linked.Token)
                            .ConfigureAwait(false);

                        if (humanResult == AskHumanResult.PendingInput)
                        {
                            break;
                        }

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

                        CliStreamDiagnostic.WriteMarkupLine(
                            stderrConsole,
                            streamContent,
                            palette.ErrorMarkup(Markup.Escape($"⚠ Tool {evt.Message} failed (tolerated)")));

                        break;

                    case IntelligenceEventType.ToolResult:

                        CliStreamDiagnostic.WriteMarkupLine(
                            stderrConsole,
                            streamContent,
                            palette.MutedMarkup(Markup.Escape(evt.Data ?? evt.Message)));

                        break;

                    case IntelligenceEventType.SessionBound:
                    case IntelligenceEventType.ConversationBound:

                        if (evt.Data is not null && Guid.TryParse(evt.Data, out Guid boundId))
                        {

                            _ = await session
                                .SaveSessionIdAsync(
                                    boundId,
                                    (id, token) => SessionMutationRevalidator
                                        .RevalidateAsync(
                                            apiClient,
                                            id,
                                            expected: null,
                                            cancellationToken: token),
                                    cancellationToken: linked.Token)
                                .ConfigureAwait(false);

                        }

                        break;

                    case IntelligenceEventType.Result:

                        _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, palette);
                        finalText = streamContent.AnswerText;

                        break;

                    case IntelligenceEventType.Error:

                        _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, palette);
                        CliStreamDiagnostic.WriteMarkupLine(
                            stderrConsole,
                            streamContent,
                            palette.ErrorLabelMarkup(
                                Markup.Escape("Error:"),
                                Markup.Escape(FormatStreamTransportError(evt.Message))));

                        return 1;
                }
            }

            _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, palette);
            if (hitl is not null)
            {
                AskHumanResult? hitlResult = await hitl.DrainAsync(CancellationToken.None).ConfigureAwait(false);
                if (hitlResult == AskHumanResult.SubmitFailed)
                {
                    return 1;
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            hitl?.Cancel();
            _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, palette);
            if (hitl is not null)
            {
                _ = await hitl.DrainAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // W4.1: return 130 (SIGINT) only on an actual user/host cancellation. A cancellation
            // from another source (e.g. a transient client timeout surfacing as OCE) falls through
            // to the generic handler and reports a normal error (exit 1).
            return 130;
        }
        catch (OperationCanceledException)
        {

            _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, palette);
            stderrConsole.MarkupLine(
                palette.ErrorLabelMarkup(
                    Markup.Escape("Error:"),
                    Markup.Escape($"{ArcanumApiClient.StreamTimeoutMessage} {ArcanumApiClient.StreamDoctorHint}")));

            return 1;

        }
        catch (Exception ex)
        {

            _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, palette);

            CliFailure failure = CliFailureMapper.Map(ex);

            dispatcher.WriteVerbose(ex.Message);

            stderrConsole.MarkupLine(
                palette.ErrorLabelMarkup(Markup.Escape("Error:"), Markup.Escape(failure.SafeMessage)));

            return (int)failure.ExitCode;

        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        if (finalText is null)
        {

            string accumulated = streamContent.AnswerText;

            if (!string.IsNullOrEmpty(accumulated))
            {

                finalText = accumulated;

            }

        }

        if (finalText is null)
        {
            stderrConsole.MarkupLine(
                palette.ErrorLabelMarkup(
                    Markup.Escape("Error:"),
                    Markup.Escape(ResolveStreamEndedWithoutResult(receivedAnyStreamEvent))));

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

    internal static string BuildPrompt(string[] promptWords)
    {
        List<string> parts = new(promptWords.Length);

        foreach (string word in promptWords)
        {
            if (!string.IsNullOrWhiteSpace(word))
            {
                parts.Add(word.Trim());
            }
        }

        return string.Join(' ', parts);
    }

    internal static IAnsiConsole CreateStderrConsole(
        TextWriter output,
        bool colorEnabled,
        bool interactive) =>
        AnsiConsole.Create(
            new AnsiConsoleSettings
            {

                Ansi = colorEnabled
                    ? AnsiSupport.Detect
                    : AnsiSupport.No,

                ColorSystem = colorEnabled
                    ? ColorSystemSupport.Detect
                    : ColorSystemSupport.NoColors,

                Interactive = interactive
                    ? InteractionSupport.Detect
                    : InteractionSupport.No,

                Out = new AnsiConsoleOutput(output),

            });

    /// <summary>
    /// Chooses an actionable message when the NDJSON stream completes without a Result (or accumulated text).
    /// Empty streams usually mean the API never answered; mid-flight silence after events points at disconnect.
    /// </summary>
    internal static string ResolveStreamEndedWithoutResult(bool receivedAnyStreamEvent) =>
        receivedAnyStreamEvent
            ? $"{ArcanumApiClient.StreamDisconnectMessage} {ArcanumApiClient.StreamDoctorHint}"
            : $"{ArcanumApiClient.StreamEmptyResultMessage} {ArcanumApiClient.StreamUnreachableMessage} {ArcanumApiClient.StreamDoctorHint}";

    /// <summary>
    /// Appends the doctor/serve hint when the transport message is one of the known ArcanumApiClient
    /// copies. Shared with every other streaming command, so the same failure names the same remedy
    /// wherever it surfaces.
    /// </summary>
    internal static string FormatStreamTransportError(string message) =>
        CliStreamTransportHint.Append(message);

}
