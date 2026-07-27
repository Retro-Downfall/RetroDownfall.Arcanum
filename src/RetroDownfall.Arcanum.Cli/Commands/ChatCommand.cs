using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RetroDownfall.Arcanum.Cli.Commands;

[ExcludeFromCodeCoverage] // Reason: interactive multi-turn REPL; sliced helpers are covered via internal static unit tests.
public sealed class ChatCommand(
    IEyeOfTheWorld eye,
    ArcanumApiClient apiClient,
    IOptions<ArcanumSettings> arcanumSettings,
    IThemePalette themePalette,
    CliSessionManager cliSession,
    MarkdigSpectreRenderer markdig,
    IGrimoireCliInitialization grimoireBootstrapper,
    IServiceScopeFactory scopeFactory,
    ICliEnvironment cliEnvironment,
    IArcanumServeLauncher serveLauncher)
{

    private long MaxAttachFileSizeBytes =>
        ArcanumSettingClamps.MaxAttachFileSizeBytes(
            ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);

    private long MaxScryingImageBytes =>
        ArcanumSettingClamps.ScryingMaxImageBytes(
            arcanumSettings.Value.ResolveScrying().MaxImageBytes);

    private string[] AllowedScryingMimeTypes =>
        arcanumSettings.Value.Security.AllowedImageMimeTypes ?? [];

    private const string DefaultStagedOnlyPrompt = "Please review the attached files.";

    private static readonly TimeSpan McpRefreshThrottle = TimeSpan.FromSeconds(5);

    private ServeLaunchResult _serveLaunch = new(
        ServeLaunchStatus.LaunchDisabled,
        HealthProbeState.NotAttempted,
        TimeSpan.Zero,
        null,
        null);

    private IReadOnlyList<McpServerInfo> _mcpServers = [];

    private bool _mcpUnavailable;

    private DateTimeOffset _lastMcpRefresh = DateTimeOffset.MinValue;

    private static readonly HashSet<string> SlashCommandVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "/exit",
        "/quit",
        "/clear",
        "/help",
        "/new",
        "/model",
        "/look",
        "/tools",
        "/mcp",
        "/arsenal",
        "/history",
        "/resume",
        "/delete",
        "/rest",
        "/log",
        "/memory",
        "/summary",
        "/attach",
        "/mana",
    };

    /// <summary>
    /// Interactive multi-turn REPL with the Mage (streamed plain text, swapped to rendered Markdown at end of turn).
    /// </summary>
    /// <param name="model">-m, The specific model to use for this inference request.</param>
    /// <param name="new">-n, Start a new session thread, clearing the previous session at REPL startup.</param>
    /// <param name="noTools">Disable MCP-provided tools for this REPL session (built-in tools still apply).</param>
    /// <param name="unattended">Force unattended for this run (also true when <c>Arcanum:Security:Ward:UnattendedMode</c> is set). Skips ask_human blocking and uses Ward auto-deny for Forbidden Arts.</param>
    /// <param name="campaign">-c, Campaign GUID to resolve the workspace from (400 Campaign.NotFound if unknown).</param>
    /// <param name="temperature">Sampling temperature 0-2 (lower = more deterministic). Applies to every turn.</param>
    /// <param name="topP">--top-p, Nucleus sampling cutoff 0-1. Applies to every turn.</param>
    /// <param name="maxTokens">Maximum output tokens per turn.</param>
    /// <param name="seed">Seed for sampling determinism (provider support varies). Applies to every turn.</param>
    /// <param name="stop">Stop sequence(s); pass --stop multiple times for several stops.</param>
    /// <param name="responseFormat">Response format: text | json_object | json_schema.</param>
    /// <param name="presencePenalty">Presence penalty -2..2.</param>
    /// <param name="frequencyPenalty">Frequency penalty -2..2.</param>
    [Command("")]
    public async Task<int> Chat(
        CancellationToken cancellationToken,
        string? model = null,
        bool @new = false,
        bool noTools = false,
        bool unattended = false,
        string? campaign = null,
        string? temperature = null,
        string? topP = null,
        string? maxTokens = null,
        string? seed = null,
        string[]? stop = null,
        string? responseFormat = null,
        string? presencePenalty = null,
        string? frequencyPenalty = null)
    {
        if (@new)
        {
            cliSession.ClearSession();
        }

        InferenceFlagInputs flagInputs = new(temperature, topP, maxTokens, seed, stop, responseFormat, presencePenalty, frequencyPenalty);

        if (!InferenceFlagBinder.TryParse(flagInputs, themePalette, out InferenceFlagBinder.Parsed flags, out int flagsExit))
        {
            return flagsExit == 0 ? 1 : flagsExit;
        }

        unattended = OperatorFacingUnattendedMode.Resolve(
            unattended,
            arcanumSettings.Value.Security.Ward);

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(campaign))
        {

            if (!Guid.TryParse(campaign, out Guid parsedCampaignId))
            {
                AnsiConsole.MarkupLine(
                    themePalette.ErrorLabelMarkup(Markup.Escape("Error:"), Markup.Escape("--campaign must be a valid GUID.")));

                return 1;
            }

            campaignId = parsedCampaignId;

        }

        IAnsiConsole stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        try
        {
            await grimoireBootstrapper.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MissingMasterApiKeyException ex)
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorLabelMarkup(Markup.Escape("Error:"), Markup.Escape(ex.Message)));

            return 1;

        }

        _serveLaunch = await serveLauncher.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        SessionMut session = new()
        {
            CurrentModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            DisableTools = noTools,
            CampaignId = campaignId,
        };

        await WriteStartupBannerAsync(session, unattended, flags, cancellationToken).ConfigureAwait(false);

        HashSet<string> stagedFiles = new(StringComparer.Ordinal);

        HashSet<string> stagedImages = new(StringComparer.Ordinal);

        int exitCode = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await RefreshMcpServersThrottledAsync(cancellationToken).ConfigureAwait(false);

            if (cliEnvironment.IsInteractive)
            {
                string statusBar = StatusBarRenderer.RenderCompact(
                    session.CurrentModel ?? arcanumSettings.Value.DefaultModel,
                    _mcpServers.Count(s => s.State == McpServerState.Running),
                    _mcpServers.Count,
                    _mcpUnavailable,
                    _serveLaunch.Status,
                    themePalette);

                AnsiConsole.MarkupLine(statusBar);
            }

            string? raw;

            int stagedCount = stagedFiles.Count + stagedImages.Count;

            string promptMarkup = stagedCount > 0
                ? $"{themePalette.HighlightMarkup(Markup.Escape($"[{stagedCount} file(s) staged]"))} {themePalette.HeadingBoldMarkup(Markup.Escape("Mage"))} >"
                : $"{themePalette.HeadingBoldMarkup(Markup.Escape("Mage"))} >";

            if (cliEnvironment.ShouldShowManaBar
                && TryGetManaBarContextLimit(session, out int manaContextLimit))
            {
                int used = session.SessionMana?.TotalTokens ?? 0;

                RenderManaBarLine(session, used, manaContextLimit);
            }

            void OnReplCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
            }

            Console.CancelKeyPress += OnReplCancelKeyPress;

            try
            {
                raw = CliLineReader.ReadLine(promptMarkup, allowEmpty: true);
            }
            catch (InvalidOperationException)
            {
                await PrintExitSummaryAsync(session, cancellationToken).ConfigureAwait(false);

                return exitCode;
            }
            finally
            {
                Console.CancelKeyPress -= OnReplCancelKeyPress;
            }

            if (raw is null)
            {

                AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Turn cancelled at prompt.")));

                continue;

            }

            string prompt = raw.Trim();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                if (stagedFiles.Count == 0 && stagedImages.Count == 0)
                {
                    continue;
                }

                prompt = DefaultStagedOnlyPrompt;
            }

            if (prompt.Length > 0 && prompt[0] == '/')
            {
                (bool handled, bool exitRepl) = await TrySlashCommandAsync(
                        prompt,
                        stagedFiles,
                        session,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (exitRepl)
                {
                    break;
                }

                if (handled)
                {
                    continue;
                }
            }

            string cwdForAt = Environment.CurrentDirectory;

            MatchCollection atMatches = Regex.Matches(prompt, @"(?<=^|\s)@([^\s]+)", RegexOptions.CultureInvariant);

            for (int mi = atMatches.Count - 1; mi >= 0; mi--)
            {
                Match match = atMatches[mi];

                if (!match.Success)
                {
                    continue;
                }

                string tokenPath = match.Groups[1].Value;

                string fullPath;

                try
                {
                    fullPath = Path.GetFullPath(Path.Combine(cwdForAt, tokenPath));
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    AnsiConsole.MarkupLine(
                        themePalette.ErrorLabelMarkup(
                            Markup.Escape($"@{tokenPath}"),
                            Markup.Escape($"could not be resolved as a path ({ex.GetType().Name}). The literal token was kept in the prompt.")));

                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    AnsiConsole.MarkupLine(
                        themePalette.ErrorLabelMarkup(
                            Markup.Escape($"@{tokenPath}"),
                            Markup.Escape($"not found at {fullPath}; the literal token was kept in the prompt. Use /attach to browse interactively.")));

                    continue;
                }

                if (ScryingFocusStager.IsImagePath(fullPath))
                {
                    ScryingFocusStager.StagingResult sizeCheck = ScryingFocusStager.CheckSize(fullPath, MaxScryingImageBytes);

                    if (sizeCheck.Error is not null)
                    {
                        WriteCannotStageScryingFocus(Path.GetFileName(fullPath), sizeCheck.Error);

                        continue;
                    }

                    stagedImages.Add(fullPath);

                    string sizeLabel = ScryingFocusStager.FormatByteCount(sizeCheck.FileSizeBytes ?? 0);

                    AnsiConsole.MarkupLine(
                        $"{themePalette.HighlightMarkup(Markup.Escape("Scrying focus:"))} {themePalette.TextMarkup(Markup.Escape($"{Path.GetFileName(fullPath)} ({sizeLabel})"))}");

                    prompt = prompt.Remove(match.Index, match.Length);

                    continue;
                }

                long len = new FileInfo(fullPath).Length;

                if (len > MaxAttachFileSizeBytes)
                {
                    WriteCannotStageTooLarge(Path.GetFileName(fullPath), MaxAttachFileSizeBytes);
                }
                else
                {
                    stagedFiles.Add(fullPath);

                    AnsiConsole.MarkupLine(
                        $"{themePalette.HighlightMarkup(Markup.Escape("Staged:"))} {themePalette.TextMarkup(Markup.Escape(Path.GetFileName(fullPath)))}");
                }

                prompt = prompt.Remove(match.Index, match.Length);
            }

            prompt = prompt.Trim();

            List<AttachedFileDto>? attachedFilesForRequest = null;

            bool attachFailed = false;

            if (stagedFiles.Count > 0)
            {
                string cwd = Environment.CurrentDirectory;

                List<AttachedFileDto> attached = new();

                List<string> relativePathsForFooter = new();

                foreach (string file in stagedFiles.OrderBy(f => f, StringComparer.Ordinal))
                {
                    string fileName = Path.GetFileName(file);

                    try
                    {
                        long len = new FileInfo(file).Length;

                        if (len > MaxAttachFileSizeBytes)
                        {
                            WriteCannotStageTooLarge(fileName, MaxAttachFileSizeBytes);

                            attachFailed = true;

                            continue;
                        }

                        string contents = File.ReadAllText(file, Encoding.UTF8);

                        string relativePath = Path.GetRelativePath(cwd, file);

                        attached.Add(new AttachedFileDto(relativePath, contents));

                        relativePathsForFooter.Add(relativePath);
                    }
                    catch (DecoderFallbackException ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"{themePalette.ErrorMarkup(Markup.Escape(file + ":"))} {themePalette.TextMarkup(Markup.Escape("file is not valid UTF-8 text: " + ex.Message))}");

                        attachFailed = true;
                    }
                    catch (IOException ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"{themePalette.ErrorMarkup(Markup.Escape(file + ":"))} {themePalette.TextMarkup(Markup.Escape(ex.Message))}");

                        attachFailed = true;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        AnsiConsole.MarkupLine($"{themePalette.ErrorMarkup(Markup.Escape(file + ":"))} {themePalette.TextMarkup(Markup.Escape(ex.Message))}");

                        attachFailed = true;
                    }
                }

                if (attachFailed && relativePathsForFooter.Count == 0)
                {
                    stagedFiles.Clear();

                    exitCode = 1;

                    continue;
                }

                if (relativePathsForFooter.Count > 0)
                {
                    prompt += $"\n\n[Attached Files: {string.Join(", ", relativePathsForFooter)}]";

                    attachedFilesForRequest = attached;
                }
            }

            List<ScryingFocusDto>? scryingFociForRequest = null;

            if (stagedImages.Count > 0)
            {
                List<ScryingFocusDto> foci = new();

                bool anyImageFailed = false;

                foreach (string imagePath in stagedImages.OrderBy(f => f, StringComparer.Ordinal))
                {
                    ScryingFocusStager.StagingResult staged = ScryingFocusStager.Stage(
                        imagePath,
                        MaxScryingImageBytes,
                        AllowedScryingMimeTypes);

                    if (staged.Error is not null)
                    {
                        WriteCannotStageScryingFocus(Path.GetFileName(imagePath), staged.Error);

                        anyImageFailed = true;

                        continue;
                    }

                    foci.Add(staged.Focus!);
                }

                if (anyImageFailed && foci.Count == 0 && attachedFilesForRequest is null)
                {
                    stagedFiles.Clear();

                    stagedImages.Clear();

                    exitCode = 1;

                    continue;
                }

                if (foci.Count > 0)
                {
                    scryingFociForRequest = foci;
                }
            }

            stagedFiles.Clear();

            stagedImages.Clear();

            bool turnOk = await RunTurnAsync(
                    prompt,
                    session,
                    unattended,
                    stderrConsole,
                    cancellationToken,
                    flags,
                    attachedFilesForRequest,
                    scryingFociForRequest)
                .ConfigureAwait(false);

            if (!turnOk)
            {
                exitCode = 1;
            }
        }

        await PrintExitSummaryAsync(session, cancellationToken).ConfigureAwait(false);

        return exitCode;
    }

    private async Task<(bool Handled, bool ExitRepl)> TrySlashCommandAsync(
        string prompt,
        HashSet<string> stagedFiles,
        SessionMut session,
        CancellationToken cancellationToken)
    {
        int sp = prompt.AsSpan().IndexOfAny(' ', '\t');

        string verb = sp < 0 ? prompt : prompt[..sp];

        if (!SlashCommandVerbs.Contains(verb))
        {
            return (false, false);
        }

        string tail = sp < 0 ? string.Empty : prompt[(sp + 1)..].Trim();

        if (verb.Equals("/exit", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            return (true, true);
        }

        if (verb.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.Clear();

            return (true, false);
        }

        if (verb.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            RenderHelp();

            return (true, false);
        }

        if (verb.Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            cliSession.ClearSession();

            session.MemoryCompressed = false;

            session.SessionMana = null;

            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Started new session.")));

            return (true, false);
        }

        if (verb.Equals("/model", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(tail))
            {
                if (!cliEnvironment.IsInteractive)
                {
                    AnsiConsole.MarkupLine(
                        themePalette.ErrorMarkup(Markup.Escape("Interactive model selection is not available when stdout is redirected. Provide a model name (e.g., /model llama3).")));

                    return (true, false);
                }

                ProviderSettings[] providers = arcanumSettings.Value.Providers ?? [];

                var selection = new SelectionPrompt<string>().Title("Select the active model:");

                bool anyGroup = false;

                foreach (ProviderSettings provider in providers)
                {
                    string[] models = [.. (provider.Models ?? []).Select(static m => m.Name)];

                    if (models.Length == 0)
                    {
                        continue;
                    }

                    anyGroup = true;

                    _ = selection.AddChoiceGroup(provider.Name, models);
                }

                if (!anyGroup)
                {
                    AnsiConsole.MarkupLine(
                        themePalette.ErrorMarkup(Markup.Escape("No models are configured under Arcanum:Providers.")));

                    return (true, false);
                }

                string picked = AnsiConsole.Prompt(selection);

                session.CurrentModel = picked;

                AnsiConsole.MarkupLine(
                    $"{themePalette.HighlightMarkup(Markup.Escape("Active model:"))} {themePalette.TextMarkup(Markup.Escape(session.CurrentModel!))}");

                return (true, false);
            }

            session.CurrentModel = tail.Trim();

            AnsiConsole.MarkupLine(
                $"{themePalette.HighlightMarkup(Markup.Escape("Model override:"))} {themePalette.TextMarkup(Markup.Escape(session.CurrentModel!))}");

            return (true, false);
        }

        if (verb.Equals("/look", StringComparison.OrdinalIgnoreCase))
        {
            PatternSnapshot snapshot = await eye
                .PerceivePatternAsync(Environment.CurrentDirectory, cancellationToken)
                .ConfigureAwait(false);

            PatternSnapshotMarkup.WritePatternSnapshot(snapshot, themePalette);

            return (true, false);
        }

        if (verb.Equals("/tools", StringComparison.OrdinalIgnoreCase))
        {
            session.DisableTools = !session.DisableTools;

            AnsiConsole.MarkupLine(
                themePalette.HighlightMarkup(
                    Markup.Escape($"MCP tools {(session.DisableTools ? "disabled" : "enabled")}.")));

            return (true, false);
        }

        if (verb.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            if (!tail.Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine(
                    $"{themePalette.HighlightMarkup(Markup.Escape("Usage:"))} {themePalette.MutedMarkup(Markup.Escape("/mcp reload"))}");

                return (true, false);
            }

            OptionalWorkspaceRequest reloadBody = new(WorkingDirectory: Environment.CurrentDirectory);

            Result<string> reloadResult = await apiClient.ReloadMcpAsync(reloadBody, cancellationToken).ConfigureAwait(false);

            if (reloadResult.IsSuccess)
            {
                AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape(reloadResult.Value)));
            }
            else
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(reloadResult.Error));
            }

            return (true, false);
        }

        if (verb.Equals("/arsenal", StringComparison.OrdinalIgnoreCase))
        {
            OptionalWorkspaceRequest arsenalBody = new(WorkingDirectory: Environment.CurrentDirectory);

            Result<WorkspaceArsenalDto> arsenalResult =
                await apiClient.GetWorkspaceArsenalAsync(arsenalBody, cancellationToken).ConfigureAwait(false);

            if (arsenalResult.IsFailure)
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(arsenalResult.Error));

                return (true, false);
            }

            RenderArsenalTree(arsenalResult.Value);

            return (true, false);
        }

        if (verb.Equals("/history", StringComparison.OrdinalIgnoreCase))
        {
            Result<SessionQueryResult> historyResult =
                await apiClient.QuerySessionsAsync(50, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (historyResult.IsFailure)
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(historyResult.Error));

                return (true, false);
            }

            if (historyResult.Value.Summaries.Length == 0)
            {
                AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No past sessions found.")));

                return (true, false);
            }

            Table historyTable = new();

            historyTable.Border(TableBorder.Rounded);

            historyTable.BorderColor(themePalette.Muted);

            historyTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("ID")));

            historyTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Title")));

            historyTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Updated")));

            historyTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Entries")));

            foreach (SessionSummaryDto row in historyResult.Value.Summaries)
            {
                string idShort = row.Id.ToString("N")[..8].ToUpperInvariant();

                string updatedLocal = row.UpdatedAt.ToLocalTime().ToString("g");

                string title = string.IsNullOrWhiteSpace(row.Title) ? "(untitled)" : row.Title;

                historyTable.AddRow(
                    Markup.Escape(idShort),
                    Markup.Escape(title),
                    Markup.Escape(updatedLocal),
                    Markup.Escape(row.EntryCount.ToString(CultureInfo.InvariantCulture)));
            }

            AnsiConsole.Write(historyTable);

            return (true, false);
        }

        if (verb.Equals("/resume", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(tail))
            {
                AnsiConsole.MarkupLine(
                    $"{themePalette.HighlightMarkup(Markup.Escape("Usage:"))} {themePalette.MutedMarkup(Markup.Escape("/resume <id>"))}");

                return (true, false);
            }

            (bool resumeOk, Guid resumeId, string? resumeErr) =
                await TryResolveSessionIdForSlashAsync(tail, cancellationToken).ConfigureAwait(false);

            if (!resumeOk)
            {
                if (resumeErr is not null)
                {
                    AnsiConsole.MarkupLine(resumeErr);
                }

                return (true, false);
            }

            cliSession.SaveSessionId(resumeId);

            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Resumed session.")));

            return (true, false);
        }

        if (verb.Equals("/delete", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(tail))
            {
                AnsiConsole.MarkupLine(
                    $"{themePalette.HighlightMarkup(Markup.Escape("Usage:"))} {themePalette.MutedMarkup(Markup.Escape("/delete <id>"))}");

                return (true, false);
            }

            (bool delOk, Guid deleteId, string? deleteErr) =
                await TryResolveSessionIdForSlashAsync(tail, cancellationToken).ConfigureAwait(false);

            if (!delOk)
            {
                if (deleteErr is not null)
                {
                    AnsiConsole.MarkupLine(deleteErr);
                }

                return (true, false);
            }

            Result archiveResult = await apiClient.ArchiveSessionAsync(deleteId, cancellationToken).ConfigureAwait(false);

            if (archiveResult.IsFailure)
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(archiveResult.Error));

                return (true, false);
            }

            Guid? active = cliSession.GetLastSessionId();

            if (active == deleteId)
            {
                cliSession.ClearSession();
            }

            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Session archived.")));

            return (true, false);
        }

        if (verb.Equals("/rest", StringComparison.OrdinalIgnoreCase))
        {
            Guid? activeSessionId = cliSession.GetLastSessionId();

            if (activeSessionId is null)
            {
                AnsiConsole.MarkupLine(
                    themePalette.HighlightMarkup(
                        Markup.Escape(
                            "No active session. Send a message first or use /resume to bind a session.")));

                return (true, false);
            }

            Result restResult = await apiClient
                .RestAsync(activeSessionId.Value, cancellationToken)
                .ConfigureAwait(false);

            if (restResult.IsFailure)
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(restResult.Error));

                return (true, false);
            }

            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Initiating long rest... Memory consolidation queued.")));

            AnsiConsole.Write(new Rule().RuleStyle(themePalette.MutedStyle()));

            return (true, false);
        }

        if (verb.Equals("/mana", StringComparison.OrdinalIgnoreCase))
        {
            await TryShowManaPanelAsync(session, cancellationToken).ConfigureAwait(false);

            return (true, false);
        }

        if (verb.Equals("/log", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("/memory", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("/summary", StringComparison.OrdinalIgnoreCase))
        {
            string panelTitle = verb.Equals("/log", StringComparison.OrdinalIgnoreCase)
                ? "Campaign Log"
                : "Memory Summary";

            await TryShowCampaignSummaryPanelAsync(panelTitle, cancellationToken).ConfigureAwait(false);

            return (true, false);
        }

        if (verb.Equals("/attach", StringComparison.OrdinalIgnoreCase))
        {
            if (!cliEnvironment.IsInteractive)
            {
                AnsiConsole.MarkupLine(
                    themePalette.ErrorMarkup(Markup.Escape("Interactive file attachment is not available when stdout is redirected.")));

                return (true, false);
            }

            RunAttachBrowser(stagedFiles, Environment.CurrentDirectory, MaxAttachFileSizeBytes);

            return (true, false);
        }

        throw new InvalidOperationException($"Unhandled whitelisted slash verb: {verb}");
    }

    private async Task<(bool Ok, Guid Id, string? ErrorMarkup)> TryResolveSessionIdForSlashAsync(
        string tail,
        CancellationToken cancellationToken)
    {
        string t = tail.Trim();

        if (Guid.TryParse(t, out Guid direct))
        {
            return (true, direct, null);
        }

        if (IsEightCharHexDigitPrefix(t))
        {
            Result<SessionQueryResult> listResult =
                await apiClient.QuerySessionsAsync(200, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (listResult.IsFailure)
            {
                return (false, default, themePalette.ErrorMarkup(listResult.Error));
            }

            List<SessionSummaryDto> matches = listResult.Value.Summaries
                .Where(c => c.Id.ToString("N").StartsWith(t, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                return (false, default, themePalette.ErrorMarkup(Markup.Escape("No session matches that ID.")));
            }

            if (matches.Count > 1)
            {
                return (
                    false,
                    default,
                    themePalette.ErrorMarkup(
                        Markup.Escape("Multiple sessions match that ID. Please provide the full Guid.")));
            }

            return (true, matches[0].Id, null);
        }

        return (
            false,
            default,
            themePalette.ErrorMarkup(
                Markup.Escape("Invalid id. Provide a full Guid or the first 8 hex characters (no dashes).")));
    }

    internal static bool IsEightCharHexDigitPrefix(string t)
    {
        if (t.Length != 8)
        {
            return false;
        }

        for (int i = 0; i < 8; i++)
        {
            if (!Uri.IsHexDigit(t[i]))
            {
                return false;
            }
        }

        return true;
    }

    private async Task WriteStartupBannerAsync(
        SessionMut session,
        bool unattended,
        InferenceFlagBinder.Parsed flags,
        CancellationToken cancellationToken)
    {

        await RefreshMcpServersThrottledAsync(cancellationToken, force: true).ConfigureAwait(false);

        List<string> overrides = CollectInferenceOverrides(flags);

        string modelLabel = session.CurrentModel ?? arcanumSettings.Value.DefaultModel ?? "(first configured)";

        BannerContext ctx = new(
            _serveLaunch.Status,
            _serveLaunch.Health,
            ArcanumLocalApiAddress.ResolveBaseUrl(arcanumSettings.Value.Host),
            modelLabel,
            session.CampaignId,
            unattended,
            session.DisableTools,
            overrides,
            _mcpServers.Count(s => s.State == McpServerState.Running),
            _mcpServers.Count,
            _mcpUnavailable,
            ArcanumBuildInfo.InformationalVersion,
            themePalette);

        AnsiConsole.Write(ArcanumBannerRenderer.Render(ctx));

        AnsiConsole.WriteLine();

    }

    private async Task RefreshMcpServersThrottledAsync(CancellationToken cancellationToken, bool force = false)
    {

        if (!force && DateTimeOffset.UtcNow - _lastMcpRefresh < McpRefreshThrottle)
        {
            return;
        }

        _lastMcpRefresh = DateTimeOffset.UtcNow;

        try
        {
            Result<IReadOnlyList<McpServerInfo>> result = await apiClient
                .GetMcpServersAsync(cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess && result.Value is not null)
            {
                _mcpServers = result.Value;

                _mcpUnavailable = false;
            }
            else
            {
                _mcpUnavailable = true;

                _mcpServers = [];
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _mcpUnavailable = true;

            _mcpServers = [];
        }

    }

    private static List<string> CollectInferenceOverrides(InferenceFlagBinder.Parsed flags)
    {

        List<string> overrides = new();

        if (flags.Temperature is { } t)
        {
            overrides.Add($"temperature={t.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        if (flags.TopP is { } tp)
        {
            overrides.Add($"top_p={tp.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        if (flags.MaxOutputTokens is { } mx)
        {
            overrides.Add($"max_tokens={mx.ToString(CultureInfo.InvariantCulture)}");
        }

        if (flags.Seed is { } sd)
        {
            overrides.Add($"seed={sd.ToString(CultureInfo.InvariantCulture)}");
        }

        if (flags.PresencePenalty is { } pp)
        {
            overrides.Add($"presence_penalty={pp.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        if (flags.FrequencyPenalty is { } fp)
        {
            overrides.Add($"frequency_penalty={fp.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrEmpty(flags.ResponseFormat))
        {
            overrides.Add($"response_format={flags.ResponseFormat}");
        }

        if (flags.Stop is { Count: > 0 } stops)
        {
            overrides.Add($"stop=[{string.Join(", ", stops)}]");
        }

        return overrides;

    }

    /// <summary>
    /// Live layout requires an interactive color terminal with enough space.
    /// Trusts <see cref="ICliEnvironment.IsInteractive"/> (already encodes redirect)
    /// rather than re-checking <see cref="Console.IsOutputRedirected"/>.
    /// </summary>
    internal static bool ShouldUseLiveLayout(ICliEnvironment env, IAnsiConsole console) =>
        env.IsInteractive
        && env.ColorEnabled
        && console.Profile.Width >= 100
        && console.Profile.Height >= 24;

    private void RenderHelp()
    {
        Table table = new();

        table.Border(TableBorder.Rounded);

        table.AddColumn(themePalette.MutedMarkup(Markup.Escape("Command")));

        table.AddColumn(themePalette.MutedMarkup(Markup.Escape("Description")));

        table.AddRow("/exit, /quit", "Leave the REPL.");

        table.AddRow("/clear", "Clear the terminal screen.");

        table.AddRow("/help", "Show this table.");

        table.AddRow("/new", "Clear session file; next turn starts a new session thread.");

        table.AddRow("/history", "List recent sessions (time travel).");

        table.AddRow(
            "/resume " + themePalette.HighlightMarkup(Markup.Escape("<id>")),
            "Continue a past session (full Guid or 8-char hex prefix).");

        table.AddRow(
            "/delete " + themePalette.HighlightMarkup(Markup.Escape("<id>")),
            "Archive a session from Grimoire (full Guid or 8-char prefix).");

        table.AddRow("/rest", "Manually trigger memory consolidation for the current session.");

        table.AddRow("/log", "View the Campaign Log (summarized history) for this session.");

        table.AddRow("/mana", "Token usage for this REPL session and session lifetime (Grimoire).");

        table.AddRow("/memory, /summary", "View the Campaign Summary (compressed memory context) for this session.");

        table.AddRow(
            "/model " + themePalette.HighlightMarkup(Markup.Escape("[<name>]")),
            "Pick from configured providers (no args) or set override by name.");

        table.AddRow("/look", "Eye of the World snapshot for the current directory.");

        table.AddRow(
            "/tools",
            "Toggle MCP tools ("
                + themePalette.HighlightMarkup(Markup.Escape("PingRequest.disableMcpTools"))
                + ").");

        table.AddRow(
            "/mcp reload",
            "Daemon: dispose MCP partitions, re-bootstrap global "
                + themePalette.HighlightMarkup(Markup.Escape("mcp.json"))
                + ".");

        table.AddRow("/arsenal", "Daemon: spells, native tools, and MCP server status.");

        table.AddRow("/attach", "Open interactive file browser to stage files for the next prompt.");

        table.AddRow(
            "@" + themePalette.HighlightMarkup(Markup.Escape("path/to/image.png")),
            "Stage a Scrying focus (image) for the next prompt; requires a vision-capable model.");

        AnsiConsole.Write(table);
    }

    private void WriteCannotStageTooLarge(string fileName, long maxAttachFileSizeBytes)
    {
        AnsiConsole.MarkupLine(
            themePalette.ErrorMarkup(
                Markup.Escape(
                    $"Cannot stage {fileName}: File exceeds the configured limit ({maxAttachFileSizeBytes} bytes).")));
    }

    private void WriteCannotStageScryingFocus(string fileName, string reason)
    {
        AnsiConsole.MarkupLine(
            themePalette.ErrorMarkup(
                Markup.Escape($"Cannot stage {fileName}: {reason} The literal token was kept in the prompt.")));
    }

    private string FormatBrowseItem(BrowseItem item)
    {
        return item.Kind switch
        {
            BrowseKind.Up => themePalette.HighlightMarkup(Markup.Escape(".. (Up one directory)")),
            BrowseKind.Directory => themePalette.HighlightMarkup(
                Markup.Escape(
                    Path.GetFileName(
                        item.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    + "/")),
            BrowseKind.File => Markup.Escape(Path.GetFileName(item.FullPath)),
            BrowseKind.Cancel => themePalette.ErrorMarkup(Markup.Escape("< Cancel >")),
            _ => "?",
        };
    }

    private void RunAttachBrowser(HashSet<string> stagedFiles, string initialDirectory, long maxAttachFileSizeBytes)
    {
        if (!cliEnvironment.IsInteractive)
        {
            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(Markup.Escape("Interactive file attachment is not available when stdout is redirected. Pass file paths directly on the command line.")));

            return;
        }

        string currentBrowseDir = Path.GetFullPath(initialDirectory);

        while (true)
        {
            string[] dirs;

            string[] files;

            try
            {
                dirs = Directory.GetDirectories(currentBrowseDir);

                files = Directory.GetFiles(currentBrowseDir);
            }
            catch (UnauthorizedAccessException)
            {
                DirectoryInfo? parent = Directory.GetParent(currentBrowseDir);

                if (parent is null)
                {
                    AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("Access denied; cannot go up.")));

                    break;
                }

                currentBrowseDir = parent.FullName;

                continue;
            }

            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            List<BrowseItem> choices = new();

            if (Directory.GetParent(currentBrowseDir) is not null)
            {
                choices.Add(new BrowseItem(BrowseKind.Up, string.Empty));
            }

            foreach (string dir in dirs)
            {
                choices.Add(new BrowseItem(BrowseKind.Directory, dir));
            }

            foreach (string file in files)
            {
                choices.Add(new BrowseItem(BrowseKind.File, file));
            }

            choices.Add(new BrowseItem(BrowseKind.Cancel, string.Empty));

            SelectionPrompt<BrowseItem> selection = new SelectionPrompt<BrowseItem>()
                .Title(
                    $"{themePalette.HighlightMarkup(Markup.Escape("Browsing:"))} {themePalette.TextMarkup(Markup.Escape(currentBrowseDir))}\n{themePalette.MutedMarkup(Markup.Escape("(Type to search, Enter to select)"))}")
                .PageSize(15)
                .UseConverter(FormatBrowseItem)
                .EnableSearch();

            foreach (BrowseItem c in choices)
            {
                selection.AddChoice(c);
            }

            BrowseItem selected;

            try
            {
                selected = AnsiConsole.Prompt(selection);
            }
            catch (InvalidOperationException)
            {
                break;
            }

            switch (selected.Kind)
            {
                case BrowseKind.Cancel:

                    return;

                case BrowseKind.Up:

                    currentBrowseDir = Directory.GetParent(currentBrowseDir)!.FullName;

                    continue;

                case BrowseKind.Directory:

                    currentBrowseDir = Path.GetFullPath(selected.FullPath);

                    continue;

                case BrowseKind.File:
                {
                    string full = Path.GetFullPath(selected.FullPath);

                    string name = Path.GetFileName(full);

                    long len;

                    try
                    {
                        len = new FileInfo(full).Length;
                    }
                    catch (IOException ex)
                    {
                        AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(ex.Message)));

                        continue;
                    }

                    if (len > maxAttachFileSizeBytes)
                    {
                        WriteCannotStageTooLarge(name, maxAttachFileSizeBytes);

                        continue;
                    }

                    stagedFiles.Add(full);

                    AnsiConsole.MarkupLine(
                        $"{themePalette.HighlightMarkup(Markup.Escape("Staged:"))} {themePalette.TextMarkup(Markup.Escape(name))}");

                    return;
                }

                default:

                    throw new InvalidOperationException($"Unexpected browse kind: {selected.Kind}");
            }
        }
    }

    private enum BrowseKind
    {

        Up,

        Directory,

        File,

        Cancel,
    }

    private readonly record struct BrowseItem(BrowseKind Kind, string FullPath);

    private void RenderArsenalTree(WorkspaceArsenalDto dto)
    {
        Tree root = new(themePalette.HeadingBoldMarkup(Markup.Escape("Arcanum Arsenal")));

        TreeNode spellsNode = root.AddNode(themePalette.HeadingBoldMarkup(Markup.Escape("Spells")));

        if (dto.ActiveSpells.Count == 0)
        {
            spellsNode.AddNode(themePalette.MutedMarkup(Markup.Escape("<none>")));
        }
        else
        {
            foreach (string s in dto.ActiveSpells)
            {
                spellsNode.AddNode(Markup.Escape(s));
            }
        }

        TreeNode nativeNode = root.AddNode(themePalette.HeadingBoldMarkup(Markup.Escape("Native Tools")));

        if (dto.NativeTools.Count == 0)
        {
            nativeNode.AddNode(themePalette.MutedMarkup(Markup.Escape("<none>")));
        }
        else
        {
            foreach (string n in dto.NativeTools)
            {
                nativeNode.AddNode(Markup.Escape(n));
            }
        }

        TreeNode mcpNode = root.AddNode(themePalette.HeadingBoldMarkup(Markup.Escape("Connected MCP Servers")));

        if (dto.McpServers.Count == 0)
        {
            mcpNode.AddNode(themePalette.MutedMarkup(Markup.Escape("<none>")));
        }
        else
        {
            foreach (McpServerStatusDto srv in dto.McpServers)
            {
                string namePart = string.Equals(srv.Status, "Online", StringComparison.OrdinalIgnoreCase)
                    ? themePalette.HighlightMarkup(Markup.Escape(srv.ServerName))
                    : themePalette.ErrorMarkup(Markup.Escape(srv.ServerName));

                string suffix = themePalette.MutedMarkup(Markup.Escape($" ({srv.ToolCount} tools)"));

                TreeNode srvNode = mcpNode.AddNode($"{namePart} {suffix}");

                if (!string.IsNullOrWhiteSpace(srv.ErrorMessage))
                {
                    srvNode.AddNode(themePalette.ErrorMarkup(Markup.Escape(srv.ErrorMessage!)));
                }

                foreach (string tool in srv.ProvidedTools)
                {
                    srvNode.AddNode(Markup.Escape(tool));
                }
            }
        }

        AnsiConsole.Write(root);

        AnsiConsole.WriteLine();
    }

    private bool TryGetManaBarContextLimit(SessionMut session, out int contextWindowLimit)
    {

        if (!ProviderResolver.TryResolveProviderForModel(
                arcanumSettings.Value,
                session.CurrentModel,
                out ProviderSettings? provider,
                out _))
        {
            contextWindowLimit = 0;

            return false;

        }

        contextWindowLimit = ArcanumSettingClamps.ContextWindowLimit(provider!.ContextWindowLimit);

        return true;

    }

    private void RenderManaBarLine(SessionMut session, int usedTokens, int contextWindowLimit)
    {
        double limit = Math.Max(1, contextWindowLimit);

        double pct = Math.Clamp(usedTokens / limit * 100.0, 0.0, 100.0);

        int displayPct = (int)Math.Round(pct, MidpointRounding.AwayFromZero);

        displayPct = Math.Clamp(displayPct, 0, 100);

        const int barWidth = 20;

        int filled = (int)Math.Round(displayPct / 100.0 * barWidth);

        filled = Math.Clamp(filled, 0, barWidth);

        string filledStr = new('█', filled);

        string emptyStr = new('░', barWidth - filled);

        Color fillColor = pct < 75.0
            ? themePalette.Highlight
            : pct <= 90.0 ? themePalette.Heading : themePalette.Error;

        string fillTag = fillColor.ToMarkup();

        string mutedTag = themePalette.Muted.ToMarkup();

        // Spectre: "[[" / "]]" emit literal brackets. Closing bar "]" must be "]]" after "[/]".
        string line =
            $"{themePalette.MutedMarkup(Markup.Escape("Mana:"))} [[[{fillTag}]{filledStr}[/][{mutedTag}]{emptyStr}[/]]] {displayPct}% ({usedTokens}/{contextWindowLimit})";

        if (session.MemoryCompressed)
        {
            line +=
                $" {themePalette.MutedMarkup(Markup.Escape("\u2699 Memory Compressed"))}";
        }

        AnsiConsole.MarkupLine(line);
    }

    private async Task TryShowCampaignSummaryPanelAsync(string panelTitle, CancellationToken cancellationToken)
    {
        Guid? logSessionId = cliSession.GetLastSessionId();

        if (logSessionId is null)
        {
            AnsiConsole.MarkupLine(
                themePalette.HighlightMarkup(
                    Markup.Escape(
                        "No active session. Send a message first or use /resume to bind a session.")));

            return;
        }

        Result<SessionDetailDto> logResult = await apiClient
            .GetSessionAsync(logSessionId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (logResult.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(logResult.Error));

            return;
        }

        SessionDetailDto detail = logResult.Value;

        if (string.IsNullOrWhiteSpace(detail.Summary))
        {
            AnsiConsole.MarkupLine(
                themePalette.HighlightMarkup(
                    Markup.Escape(
                        "No campaign log exists for this session yet. Type /rest to trigger consolidation.")));

            return;
        }

        Panel logPanel = new(new Markup(Markup.Escape(detail.Summary)))
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape(panelTitle))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        };

        AnsiConsole.Write(logPanel);
    }

    private async Task<bool> RunTurnAsync(
        string prompt,
        SessionMut session,
        bool unattended,
        IAnsiConsole stderrConsole,
        CancellationToken cancellationToken,
        InferenceFlagBinder.Parsed flags,
        List<AttachedFileDto>? attachedFiles = null,
        List<ScryingFocusDto>? scryingFoci = null)
    {
        using CancellationTokenSource perTurnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;

            try
            {
                perTurnCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        Console.CancelKeyPress += OnCancelKeyPress;

        CliStreamContent streamContent = new();

        int linesPrinted = 0;

        int currentLineLen = 0;

        int width = Math.Max(1, AnsiConsole.Profile.Width);

        bool streamWithMarkdownRewrite = cliEnvironment.IsInteractive && cliEnvironment.ColorEnabled;

        string? finalText = null;

        bool cancelled = false;

        bool errored = false;

        bool submitFailed = false;

        ConsoleAskHumanCoordinator? hitl = null;

        try
        {
            string cwd = Environment.CurrentDirectory;

            PatternSnapshot snapshot = await eye
                .PerceivePatternAsync(cwd, perTurnCts.Token)
                .ConfigureAwait(false);

            ChronosyncReport chronosyncDelta;

            await using (AsyncServiceScope chronosyncScope = scopeFactory.CreateAsyncScope())
            {
                IChronosyncEngine chronosync = chronosyncScope.ServiceProvider.GetRequiredService<IChronosyncEngine>();

                chronosyncDelta = await chronosync.AnalyzeAndSyncAsync(snapshot, perTurnCts.Token).ConfigureAwait(false);
            }

            Guid? sessionId = cliSession.GetLastSessionId();

            PingRequest ping = new(
                prompt,
                session.CurrentModel,
                cwd,
                snapshot,
                sessionId,
                session.DisableTools,
                CliTerminalFormatting: true,
                UnattendedMode: unattended,
                AttachedFiles: attachedFiles,
                ChronosyncDelta: chronosyncDelta,
                Temperature: flags.Temperature,
                TopP: flags.TopP,
                MaxOutputTokens: flags.MaxOutputTokens,
                Stop: flags.Stop,
                Seed: flags.Seed,
                ResponseFormat: flags.ResponseFormat,
                PresencePenalty: flags.PresencePenalty,
                FrequencyPenalty: flags.FrequencyPenalty,
                CampaignId: session.CampaignId,
                ScryingFoci: scryingFoci);

            if (ShouldUseLiveLayout(cliEnvironment, AnsiConsole.Console))
            {
                return await RunTurnLiveAsync(
                        ping,
                        session,
                        unattended,
                        stderrConsole,
                        perTurnCts.Token)
                    .ConfigureAwait(false);
            }

            hitl = new ConsoleAskHumanCoordinator(apiClient, themePalette);
            await foreach (IntelligenceEvent evt in apiClient.AskStreamAsync(ping, perTurnCts.Token).ConfigureAwait(false))
            {
                hitl.ObserveStreamEvent(evt);
                switch (evt.Type)
                {
                    case IntelligenceEventType.Status:

                        if (string.Equals(
                                evt.Message,
                                IntelligenceStatusMessages.MemoryCompressionNotice,
                                StringComparison.Ordinal))
                        {
                            session.MemoryCompressed = true;
                        }

                        stderrConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape(evt.Message)));

                        break;

                    case IntelligenceEventType.Token:

                        string chunk = evt.Data ?? string.Empty;

                        if (chunk.Length == 0)
                        {
                            break;
                        }

                        _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, themePalette);
                        streamContent.AppendAnswer(chunk);

                        if (streamWithMarkdownRewrite)
                        {
                            AnsiConsole.Markup(Markup.Escape(chunk));

                            AdvanceLineCounter(chunk, width, ref linesPrinted, ref currentLineLen);
                        }

                        break;

                    case IntelligenceEventType.Reasoning:

                        _ = streamContent.AppendReasoning(evt);

                        break;

                    case IntelligenceEventType.ToolCall:

                        AskHumanResult humanResult = await hitl
                            .TryBeginAsync(
                                evt,
                                unattended,
                                cliEnvironment.IsInteractive,
                                perTurnCts.Token)
                            .ConfigureAwait(false);

                        if (humanResult == AskHumanResult.PendingInput)
                        {
                            break;
                        }

                        if (humanResult == AskHumanResult.SubmitFailed)
                        {
                            submitFailed = true;

                            errored = true;

                            break;
                        }

                        if (humanResult == AskHumanResult.Handled)
                        {
                            break;
                        }

                        goto case IntelligenceEventType.ToolResult;

                    case IntelligenceEventType.ToolError:

                        stderrConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape($"⚠ Tool {evt.Message} failed (tolerated)")));

                        break;

                    case IntelligenceEventType.ToolResult:

                        stderrConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape(evt.Data ?? evt.Message)));

                        break;

                    case IntelligenceEventType.SessionBound:
                    case IntelligenceEventType.ConversationBound:

                        if (evt.Data is not null && Guid.TryParse(evt.Data, out Guid boundId))
                        {
                            cliSession.SaveSessionId(boundId);
                        }

                        break;

                    case IntelligenceEventType.Result:

                        _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, themePalette);
                        if (evt.Usage is { } usageTurn)
                        {
                            session.SessionMana = AccumulateSessionMana(session.SessionMana, usageTurn);
                        }
                        else if (evt.Data is not null
                            && int.TryParse(evt.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedUsage))
                        {
                            session.SessionMana = AccumulateSessionMana(
                                session.SessionMana,
                                new ChatCompletionUsage(0, 0, parsedUsage));
                        }

                        finalText = streamContent.AnswerText;

                        break;

                    case IntelligenceEventType.Error:

                        _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, themePalette);
                        AnsiConsole.WriteLine();

                        Panel errorPanel = new(new Markup(themePalette.TextMarkup(Markup.Escape(evt.Message))))
                        {
                            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Error"))),
                            Border = BoxBorder.Rounded,
                            BorderStyle = themePalette.ErrorStyle(),
                            Padding = new Padding(1, 0, 1, 0),
                        };

                        AnsiConsole.Write(errorPanel);

                        errored = true;

                        break;
                }

                if (submitFailed)
                {
                    break;
                }
            }

            _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, themePalette);
            AskHumanResult? hitlResult = await hitl.DrainAsync(CancellationToken.None).ConfigureAwait(false);
            if (hitlResult == AskHumanResult.SubmitFailed)
            {
                submitFailed = true;
                errored = true;
            }
        }
        catch (OperationCanceledException)
        {
            hitl?.Cancel();
            _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, themePalette);
            if (hitl is not null)
            {
                _ = await hitl.DrainAsync(CancellationToken.None).ConfigureAwait(false);
            }

            cancelled = true;
        }
        catch (Exception ex)
        {

            _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, themePalette);
            AnsiConsole.WriteLine();

            Panel errorPanel = new(new Markup(themePalette.TextMarkup(Markup.Escape(ex.Message))))
            {
                Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Error"))),
                Border = BoxBorder.Rounded,
                BorderStyle = themePalette.ErrorStyle(),
                Padding = new Padding(1, 0, 1, 0),
            };

            AnsiConsole.Write(errorPanel);

            return false;

        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        if (cancelled)
        {
            AnsiConsole.WriteLine();

            AnsiConsole.Write(new Rule(themePalette.HighlightMarkup(Markup.Escape("\u29D6 Turn cancelled")))
            {
                Justification = Justify.Left,
                Style = themePalette.MutedStyle(),
            });

            return true;
        }

        if (errored)
        {
            return false;
        }

        string body = finalText ?? streamContent.AnswerText;

        if (string.IsNullOrEmpty(body))
        {
            AnsiConsole.WriteLine();

            return true;
        }

        if (streamWithMarkdownRewrite && streamContent.AnswerLength > 0)
        {

            AnsiConsole.WriteLine();

            EraseStreamedLines(linesPrinted);

            AnsiConsole.Write(markdig.Render(body));

        }
        else if (!streamWithMarkdownRewrite)
        {
            if (cliEnvironment.IsInteractive)
            {
                AnsiConsole.Write(markdig.Render(body));
            }
            else
            {
                await Console.Out.WriteLineAsync(body).ConfigureAwait(false);
            }
        }

        AnsiConsole.WriteLine();

        return true;
    }

    private async Task<bool> RunTurnLiveAsync(
        PingRequest ping,
        SessionMut session,
        bool unattended,
        IAnsiConsole stderrConsole,
        CancellationToken cancellationToken)
    {

        CliStreamContent streamContent = new();

        List<ToolDiagnosticLine> liveDiagnostics = new();

        string? finalText = null;

        bool cancelled = false;

        bool errored = false;

        bool submitFailed = false;

        ConsoleAskHumanCoordinator? hitl = null;

        System.Diagnostics.Stopwatch refreshClock = System.Diagnostics.Stopwatch.StartNew();
        StreamingRenderCadence renderCadence = new(() => refreshClock.ElapsedMilliseconds);

        ChatLayoutContext BuildCtx(bool generating) => new(
            StatusBarRenderer.RenderCompact(
                session.CurrentModel ?? arcanumSettings.Value.DefaultModel,
                _mcpServers.Count(s => s.State == McpServerState.Running),
                _mcpServers.Count,
                _mcpUnavailable,
                _serveLaunch.Status,
                themePalette),
            streamContent.AnswerText,
            streamContent.ReasoningText,
            liveDiagnostics,
            Array.Empty<IRenderable>(),
            _mcpServers,
            session.CurrentModel ?? arcanumSettings.Value.DefaultModel,
            FormatManaSummary(session),
            _serveLaunch.Status,
            themePalette,
            generating);

        try
        {
            hitl = new ConsoleAskHumanCoordinator(apiClient, themePalette);
            await AnsiConsole.Live(ChatLayoutRenderer.Build(BuildCtx(generating: true)))
                .AutoClear(true)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async live =>
                {
                    void Refresh(bool force)
                    {
                        if (!renderCadence.ShouldRefresh(force))
                        {
                            return;
                        }

                        live.UpdateTarget(ChatLayoutRenderer.Build(BuildCtx(generating: true)));

                        renderCadence.MarkRefreshed();
                    }

                    await foreach (IntelligenceEvent evt in apiClient.AskStreamAsync(ping, cancellationToken).ConfigureAwait(false))
                    {
                        hitl.ObserveStreamEvent(evt);
                        switch (evt.Type)
                        {
                            case IntelligenceEventType.Status:

                                if (string.Equals(
                                        evt.Message,
                                        IntelligenceStatusMessages.MemoryCompressionNotice,
                                        StringComparison.Ordinal))
                                {
                                    session.MemoryCompressed = true;
                                }

                                stderrConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape(evt.Message ?? string.Empty)));

                                break;

                            case IntelligenceEventType.Token:

                                string chunk = evt.Data ?? string.Empty;

                                if (chunk.Length == 0)
                                {
                                    break;
                                }

                                streamContent.AppendAnswer(chunk);

                                renderCadence.NoteChunk();

                                Refresh(force: false);

                                break;

                            case IntelligenceEventType.Reasoning:

                                if (streamContent.AppendReasoning(evt))
                                {
                                    renderCadence.NoteChunk();
                                    Refresh(force: false);
                                }

                                break;

                            case IntelligenceEventType.ToolCall:

                                AskHumanResult humanResult = await hitl
                                    .TryBeginAsync(
                                        evt,
                                        unattended,
                                        cliEnvironment.IsInteractive,
                                        cancellationToken)
                                    .ConfigureAwait(false);

                                if (humanResult == AskHumanResult.PendingInput)
                                {
                                    break;
                                }

                                if (humanResult == AskHumanResult.SubmitFailed)
                                {
                                    submitFailed = true;

                                    errored = true;

                                    break;
                                }

                                if (humanResult == AskHumanResult.Handled)
                                {
                                    Refresh(force: true);

                                    break;
                                }

                                liveDiagnostics.Add(ToolDiagnosticLine.Create(
                                    evt.Message ?? "tool",
                                    ToolDiagnosticOutcome.Succeeded,
                                    evt.Data ?? evt.Message ?? string.Empty));

                                Refresh(force: true);

                                break;

                            case IntelligenceEventType.ToolError:

                                liveDiagnostics.Add(ToolDiagnosticLine.Create(
                                    evt.Message ?? "tool",
                                    ToolDiagnosticOutcome.Failed,
                                    evt.Data ?? evt.Message ?? "failed"));

                                Refresh(force: true);

                                break;

                            case IntelligenceEventType.ToolResult:

                                liveDiagnostics.Add(ToolDiagnosticLine.Create(
                                    evt.Message ?? "tool",
                                    ToolDiagnosticOutcome.Succeeded,
                                    evt.Data ?? evt.Message ?? string.Empty));

                                Refresh(force: true);

                                break;

                            case IntelligenceEventType.SessionBound:
                            case IntelligenceEventType.ConversationBound:

                                if (evt.Data is not null && Guid.TryParse(evt.Data, out Guid boundId))
                                {
                                    cliSession.SaveSessionId(boundId);
                                }

                                break;

                            case IntelligenceEventType.Result:

                                if (evt.Usage is { } usageTurn)
                                {
                                    session.SessionMana = AccumulateSessionMana(session.SessionMana, usageTurn);
                                }
                                else if (evt.Data is not null
                                    && int.TryParse(evt.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedUsage))
                                {
                                    session.SessionMana = AccumulateSessionMana(
                                        session.SessionMana,
                                        new ChatCompletionUsage(0, 0, parsedUsage));
                                }

                                finalText = streamContent.AnswerText;

                                Refresh(force: true);

                                break;

                            case IntelligenceEventType.Error:

                                Panel errorPanel = new(new Markup(themePalette.TextMarkup(Markup.Escape(evt.Message ?? string.Empty))))
                                {
                                    Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Error"))),
                                    Border = BoxBorder.Rounded,
                                    BorderStyle = themePalette.ErrorStyle(),
                                    Padding = new Padding(1, 0, 1, 0),
                                };

                                AnsiConsole.Write(errorPanel);

                                errored = true;

                                break;
                        }

                        if (submitFailed || errored)
                        {
                            break;
                        }
                    }
                })
                .ConfigureAwait(false);

            AskHumanResult? hitlResult = await hitl.DrainAsync(CancellationToken.None).ConfigureAwait(false);
            if (hitlResult == AskHumanResult.SubmitFailed)
            {
                submitFailed = true;
                errored = true;
            }
        }
        catch (OperationCanceledException)
        {
            hitl?.Cancel();
            if (hitl is not null)
            {
                _ = await hitl.DrainAsync(CancellationToken.None).ConfigureAwait(false);
            }

            cancelled = true;
        }
        catch (Exception ex)
        {
            _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, themePalette);
            AnsiConsole.WriteLine();

            Panel errorPanel = new(new Markup(themePalette.TextMarkup(Markup.Escape(ex.Message))))
            {
                Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Error"))),
                Border = BoxBorder.Rounded,
                BorderStyle = themePalette.ErrorStyle(),
                Padding = new Padding(1, 0, 1, 0),
            };

            AnsiConsole.Write(errorPanel);

            return false;
        }

        _ = EphemeralReasoningRenderer.Flush(stderrConsole, streamContent, themePalette);
        if (cancelled)
        {
            AnsiConsole.WriteLine();

            AnsiConsole.Write(new Rule(themePalette.HighlightMarkup(Markup.Escape("\u29D6 Turn cancelled")))
            {
                Justification = Justify.Left,
                Style = themePalette.MutedStyle(),
            });

            string partial = streamContent.AnswerText;

            if (!string.IsNullOrEmpty(partial))
            {
                AnsiConsole.Write(markdig.Render(partial));

                AnsiConsole.WriteLine();
            }

            return true;
        }

        if (errored)
        {
            return false;
        }

        string body = finalText ?? streamContent.AnswerText;

        if (string.IsNullOrEmpty(body))
        {
            AnsiConsole.WriteLine();

            return true;
        }

        AnsiConsole.Write(markdig.Render(body));

        AnsiConsole.WriteLine();

        return true;

    }

    private string FormatManaSummary(SessionMut session)
    {

        if (session.SessionMana is null)
        {
            return "(untracked)";
        }

        int used = session.SessionMana.TotalTokens;

        if (TryGetManaBarContextLimit(session, out int limit) && limit > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{used}/{limit} tokens");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{used} tokens");

    }

    internal static void AdvanceLineCounter(string chunk, int width, ref int linesPrinted, ref int currentLineLen)
    {
        foreach (char c in chunk)
        {
            if (c == '\n')
            {
                linesPrinted += 1;

                currentLineLen = 0;
            }
            else if (c == '\r')
            {
                currentLineLen = 0;
            }
            else
            {
                if (currentLineLen >= width)
                {
                    linesPrinted += 1;

                    currentLineLen = 1;
                }
                else
                {
                    currentLineLen += 1;
                }
            }
        }
    }

    private static void EraseStreamedLines(int linesPrinted)
    {

        if (linesPrinted <= 0)
        {

            return;

        }

        // Move the cursor up one line and clear it, repeating for each streamed line.

        string eraseSequence =
            $"\u001b[1A\u001b[2K" +
            string.Join(string.Empty, Enumerable.Repeat("\u001b[1A\u001b[2K", linesPrinted - 1));

        AnsiConsole.Write(eraseSequence);

    }

    private sealed class SessionMut
    {

        public string? CurrentModel { get; set; }

        public bool DisableTools { get; set; }

        public bool MemoryCompressed { get; set; }

        public ChatCompletionUsage? SessionMana { get; set; }

        public Guid? CampaignId { get; set; }

    }

    internal static ChatCompletionUsage AccumulateSessionMana(ChatCompletionUsage? running, ChatCompletionUsage round)
    {
        int roundPrompt = Math.Max(0, round.PromptTokens);

        int roundCompletion = Math.Max(0, round.CompletionTokens);

        int p = SaturatingTokenAdd(running?.PromptTokens ?? 0, roundPrompt);

        int c = SaturatingTokenAdd(running?.CompletionTokens ?? 0, roundCompletion);

        // Sum the normalized provider-reported total directly, including an explicit zero.
        // Reasoning is already a completion-token subset and must never be added to this total.
        int previousTotal = running?.TotalTokens ?? 0;

        int total = SaturatingTokenAdd(previousTotal, round.TotalTokens);

        int cached = SaturatingTokenAdd(running?.CachedTokens ?? 0, round.CachedTokens);

        int reasoning = SaturatingTokenAdd(running?.ReasoningTokens ?? 0, round.ReasoningTokens);

        return new ChatCompletionUsage(p, c, total, cached, reasoning);
    }

    private static int SaturatingTokenAdd(int left, int right) =>
        (int)Math.Clamp((long)Math.Max(0, left) + Math.Max(0, right), 0L, int.MaxValue);

    private async Task TryShowManaPanelAsync(SessionMut session, CancellationToken cancellationToken)
    {
        int sessionPrompt = session.SessionMana?.PromptTokens ?? 0;

        int sessionCompletion = session.SessionMana?.CompletionTokens ?? 0;

        int sessionTotal = session.SessionMana?.TotalTokens ?? 0;

        Guid? activeId = cliSession.GetLastSessionId();

        long lifetime = 0L;

        string? lifetimeError = null;

        if (activeId is not null)
        {
            Result<SessionDetailDto> detailResult =
                await apiClient.GetSessionAsync(activeId.Value, cancellationToken).ConfigureAwait(false);

            if (detailResult.IsFailure)
            {
                lifetimeError = IThemePaletteMarkupExtensions.FormatError(detailResult.Error);
            }
            else
            {
                lifetime = detailResult.Value.TotalTokensUsed;
            }
        }

        Table inner = new();

        inner.Border(TableBorder.None);

        inner.HideHeaders();

        inner.AddColumn(new TableColumn(string.Empty).NoWrap());

        inner.AddColumn(new TableColumn(string.Empty));

        if (session.SessionMana is null)
        {
            inner.AddRow(
                themePalette.MutedMarkup(Markup.Escape("This session:")),
                themePalette.MutedMarkup(Markup.Escape("(no recorded usage yet)")));
        }
        else
        {
            inner.AddRow(
                themePalette.MutedMarkup(Markup.Escape("prompt_tokens:")),
                themePalette.TextMarkup(Markup.Escape(sessionPrompt.ToString("N0", CultureInfo.InvariantCulture))));

            inner.AddRow(
                themePalette.MutedMarkup(Markup.Escape("completion_tokens:")),
                themePalette.TextMarkup(Markup.Escape(sessionCompletion.ToString("N0", CultureInfo.InvariantCulture))));

            inner.AddRow(
                themePalette.MutedMarkup(Markup.Escape("total_tokens (session):")),
                themePalette.HighlightMarkup(Markup.Escape(sessionTotal.ToString("N0", CultureInfo.InvariantCulture))));
        }

        if (activeId is null)
        {
            inner.AddRow(
                themePalette.MutedMarkup(Markup.Escape("Session lifetime:")),
                themePalette.MutedMarkup(
                    Markup.Escape("no active session \u2014 bind one with /resume to see lifetime Grimoire totals.")));
        }
        else if (lifetimeError is not null)
        {
            inner.AddRow(
                themePalette.MutedMarkup(Markup.Escape("Session lifetime:")),
                themePalette.ErrorMarkup(Markup.Escape(lifetimeError)));
        }
        else
        {
            inner.AddRow(
                themePalette.MutedMarkup(Markup.Escape("Session lifetime:")),
                themePalette.HighlightMarkup(Markup.Escape(lifetime.ToString("N0", CultureInfo.InvariantCulture))));
        }

        Panel headerPanel = new(inner)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Mana"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
            Padding = new Padding(1, 0, 1, 0),
        };

        AnsiConsole.Write(headerPanel);

        // BarChart of session prompt / completion vs lifetime total (where available).
        if (sessionPrompt + sessionCompletion + lifetime > 0)
        {
            BarChart chart = new BarChart()
                .Width(60)
                .Label(themePalette.MutedMarkup(Markup.Escape("token mix")));

            if (sessionPrompt > 0)
            {
                chart = chart.AddItem("session prompt", sessionPrompt, themePalette.Highlight);
            }

            if (sessionCompletion > 0)
            {
                chart = chart.AddItem("session completion", sessionCompletion, themePalette.Heading);
            }

            if (lifetime > 0)
            {
                int safeLifetime = lifetime > int.MaxValue ? int.MaxValue : (int)lifetime;

                chart = chart.AddItem("session lifetime", safeLifetime, themePalette.Muted);
            }

            AnsiConsole.Write(chart);
        }

        AnsiConsole.WriteLine();
    }

    private async Task PrintExitSummaryAsync(SessionMut session, CancellationToken cancellationToken)
    {
        int sessionTotal = session.SessionMana?.TotalTokens ?? 0;

        long lifetime = 0L;

        Guid? id = cliSession.GetLastSessionId();

        if (id is not null)
        {
            try
            {
                Result<SessionDetailDto> r =
                    await apiClient.GetSessionAsync(id.Value, cancellationToken).ConfigureAwait(false);

                if (r.IsSuccess)
                {
                    lifetime = r.Value.TotalTokensUsed;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }

        if (sessionTotal <= 0 && lifetime <= 0)
        {
            return;
        }

        string exitBody =
            $"Session total_tokens: {sessionTotal.ToString(CultureInfo.InvariantCulture)}\nSession lifetime (Grimoire): {lifetime.ToString(CultureInfo.InvariantCulture)}";

        if (session.SessionMana is not null)
        {
            exitBody +=
                $"\nSession prompt_tokens: {session.SessionMana.PromptTokens.ToString(CultureInfo.InvariantCulture)}, completion_tokens: {session.SessionMana.CompletionTokens.ToString(CultureInfo.InvariantCulture)}";
        }

        Panel panel = new(new Markup(Markup.Escape(exitBody)))
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Session mana"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.MutedStyle(),
        };

        AnsiConsole.Write(panel);

        AnsiConsole.WriteLine();
    }

}
