using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class ChatCommand(
    IEyeOfTheWorld eye,
    ArcanumApiClient apiClient,
    IOptions<ArcanumSettings> arcanumSettings,
    IThemePalette themePalette,
    CliSessionManager cliSession,
    MarkdigSpectreRenderer markdig,
    IGrimoireCliInitialization grimoireBootstrapper,
    IServiceScopeFactory scopeFactory,
    ICliEnvironment cliEnvironment) : AsyncCommand<ChatCommand.Settings>
{

    private long MaxAttachFileSizeBytes =>
        ArcanumSettingClamps.MaxAttachFileSizeBytes(arcanumSettings.Value.Cli.MaxAttachFileSizeBytes);

    private const string DefaultStagedOnlyPrompt = "Please review the attached files.";

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

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.New)
        {
            cliSession.ClearSession();
        }

        if (!InferenceFlagBinder.TryParse(settings, themePalette, out InferenceFlagBinder.Parsed flags, out int flagsExit))
        {
            return flagsExit == 0 ? 1 : flagsExit;
        }

        IAnsiConsole stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        await grimoireBootstrapper.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        SessionMut session = new()
        {
            CurrentModel = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim(),
            DisableTools = settings.NoTools,
        };

        WriteStartupBanner(session, settings, flags);

        HashSet<string> stagedFiles = new(StringComparer.Ordinal);

        int exitCode = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string raw;

            string promptMarkup = stagedFiles.Count > 0
                ? $"{themePalette.HighlightMarkup(Markup.Escape($"[{stagedFiles.Count} file(s) staged]"))} {themePalette.HeadingBoldMarkup(Markup.Escape("Mage"))} >"
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
                raw = AnsiConsole.Prompt(new TextPrompt<string>(promptMarkup).AllowEmpty());
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

            string prompt = raw.Trim();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                if (stagedFiles.Count == 0)
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
                        settings,
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

            stagedFiles.Clear();

            bool turnOk = await RunTurnAsync(
                    prompt,
                    session,
                    settings,
                    stderrConsole,
                    cancellationToken,
                    flags,
                    attachedFilesForRequest)
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
        Settings settings,
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
                ProviderSettings[] providers = arcanumSettings.Value.Providers ?? [];

                var selection = new SelectionPrompt<string>().Title("Select the active model:");

                bool anyGroup = false;

                foreach (ProviderSettings provider in providers)
                {
                    string[] models = provider.Models ?? [];

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
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(reloadResult.Error.Message)));
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
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(arsenalResult.Error.Message)));

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
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(historyResult.Error.Message)));

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
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(archiveResult.Error.Message)));

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
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(restResult.Error.Message)));

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
                return (false, default, themePalette.ErrorMarkup(Markup.Escape(listResult.Error.Message)));
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

    private static bool IsEightCharHexDigitPrefix(string t)
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

    private void WriteStartupBanner(SessionMut session, Settings settings, InferenceFlagBinder.Parsed flags)
    {

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        string modelLabel = session.CurrentModel ?? arcanumSettings.Value.DefaultModel ?? "(first configured)";

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Model:")),
            themePalette.HighlightMarkup(Markup.Escape(modelLabel)));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("MCP tools:")),
            themePalette.TextMarkup(Markup.Escape(session.DisableTools ? "disabled (--no-tools)" : "enabled")));

        if (settings.Unattended)
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape("Mode:")),
                themePalette.HighlightMarkup(Markup.Escape("unattended (ask_human auto-replies)")));
        }

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

        if (overrides.Count > 0)
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape("Inference:")),
                themePalette.TextMarkup(Markup.Escape(string.Join("  ", overrides))));
        }

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Tip:")),
            themePalette.MutedMarkup(
                Markup.Escape("/help for slash commands  -  /exit to quit  -  Ctrl+C to cancel a turn")));

        Panel banner = new(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Arcanum chat"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HeadingStyle(),
            Padding = new Padding(1, 0, 1, 0),
        };

        AnsiConsole.Write(banner);

        AnsiConsole.WriteLine();

    }

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

        AnsiConsole.Write(table);
    }

    private void WriteCannotStageTooLarge(string fileName, long maxAttachFileSizeBytes)
    {
        AnsiConsole.MarkupLine(
            themePalette.ErrorMarkup(
                Markup.Escape(
                    $"Cannot stage {fileName}: File exceeds the configured limit ({maxAttachFileSizeBytes} bytes).")));
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

        string line =
            $"{themePalette.MutedMarkup(Markup.Escape("Mana:"))} [[[{fillTag}]{filledStr}[/][{mutedTag}]{emptyStr}[/]] {displayPct}% ({usedTokens}/{contextWindowLimit})";

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
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(logResult.Error.Message)));

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
        Settings settings,
        IAnsiConsole stderrConsole,
        CancellationToken cancellationToken,
        InferenceFlagBinder.Parsed flags,
        List<AttachedFileDto>? attachedFiles = null)
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

        StringBuilder full = new();

        int linesPrinted = 0;

        int currentLineLen = 0;

        int width = Math.Max(1, AnsiConsole.Profile.Width);

        bool streamWithMarkdownRewrite = cliEnvironment.IsInteractive && cliEnvironment.ColorEnabled;

        string? finalText = null;

        bool cancelled = false;

        bool errored = false;

        bool submitFailed = false;

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

                chronosyncDelta = await chronosync.AnalyzeAndSyncAsync(snapshot).ConfigureAwait(false);
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
                UnattendedMode: settings.Unattended,
                AttachedFiles: attachedFiles,
                ChronosyncDelta: chronosyncDelta,
                Temperature: flags.Temperature,
                TopP: flags.TopP,
                MaxOutputTokens: flags.MaxOutputTokens,
                Stop: flags.Stop,
                Seed: flags.Seed,
                ResponseFormat: flags.ResponseFormat,
                PresencePenalty: flags.PresencePenalty,
                FrequencyPenalty: flags.FrequencyPenalty);

            await foreach (IntelligenceEvent evt in apiClient.AskStreamAsync(ping, perTurnCts.Token).ConfigureAwait(false))
            {
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

                        full.Append(chunk);

                        if (streamWithMarkdownRewrite)
                        {
                            AnsiConsole.Markup(Markup.Escape(chunk));

                            AdvanceLineCounter(chunk, width, ref linesPrinted, ref currentLineLen);
                        }

                        break;

                    case IntelligenceEventType.ToolCall:

                        AskHumanResult humanResult = await AskHumanToolCallStreamHandler
                            .TryHandleAskHumanAsync(
                                evt,
                                settings.Unattended,
                                cliEnvironment.IsInteractive,
                                apiClient,
                                themePalette,
                                perTurnCts.Token)
                            .ConfigureAwait(false);

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

                        finalText = full.ToString();

                        break;

                    case IntelligenceEventType.Error:

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
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
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

        string body = finalText ?? full.ToString();

        if (string.IsNullOrEmpty(body))
        {
            AnsiConsole.WriteLine();

            return true;
        }

        if (streamWithMarkdownRewrite && full.Length > 0)
        {
            if (linesPrinted > 0)
            {
                AnsiConsole.Cursor.Move(CursorDirection.Up, linesPrinted);
            }

            Console.Write("\r\u001b[0J");

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

    private static void AdvanceLineCounter(string chunk, int width, ref int linesPrinted, ref int currentLineLen)
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

    private sealed class SessionMut
    {

        public string? CurrentModel { get; set; }

        public bool DisableTools { get; set; }

        public bool MemoryCompressed { get; set; }

        public ChatCompletionUsage? SessionMana { get; set; }

    }

    private static ChatCompletionUsage AccumulateSessionMana(ChatCompletionUsage? running, ChatCompletionUsage round)
    {
        int p = (running?.PromptTokens ?? 0) + round.PromptTokens;

        int c = (running?.CompletionTokens ?? 0) + round.CompletionTokens;

        // Prefer the provider-reported total when the round itself reports one; providers can
        // (and do) include extra tokens beyond prompt+completion (reasoning tokens, cached
        // prefills, tool-call framing, etc.) and re-summing locally would understate usage.
        // When the provider returned 0/missing, fall back to the recomputed p+c so the bar
        // still reflects observable activity.
        int previousTotal = running?.TotalTokens ?? 0;

        int roundTotal = round.TotalTokens > 0 ? round.TotalTokens : (round.PromptTokens + round.CompletionTokens);

        int total = previousTotal + roundTotal;

        if (total < p + c)
        {
            total = p + c;
        }

        return new ChatCompletionUsage(p, c, total);
    }

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
                lifetimeError = detailResult.Error.Message;
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

    public sealed class Settings : CommandSettings, IInferenceFlagInputs
    {
        [CommandOption("-m|--model")]
        [Description("The specific model to use for this inference request")]
        public string? Model { get; init; }

        [CommandOption("-n|--new")]
        [Description("Start a new session thread, clearing the previous session at REPL startup.")]
        public bool New { get; init; }

        [CommandOption("--no-tools")]
        [Description("Disable MCP-provided tools for this REPL session (built-in tools still apply).")]
        public bool NoTools { get; init; }

        [CommandOption("--unattended")]
        [Description("Do not block for ask_human; auto-reply so the Mage proceeds without a live operator.")]
        public bool Unattended { get; set; }

        [CommandOption("--temperature <VALUE>")]
        [Description("Sampling temperature 0\u20132 (lower = more deterministic). Applies to every turn.")]
        public string? Temperature { get; init; }

        [CommandOption("--top-p <VALUE>")]
        [Description("Nucleus sampling cutoff 0\u20131. Applies to every turn.")]
        public string? TopP { get; init; }

        [CommandOption("--max-tokens <N>")]
        [Description("Maximum output tokens per turn.")]
        public string? MaxTokens { get; init; }

        [CommandOption("--seed <N>")]
        [Description("Seed for sampling determinism (provider support varies). Applies to every turn.")]
        public string? Seed { get; init; }

        [CommandOption("--stop <SEQUENCE>")]
        [Description("Stop sequence(s); pass --stop multiple times for several stops.")]
        public string[]? Stop { get; init; }

        [CommandOption("--response-format <KIND>")]
        [Description("Response format: text | json_object | json_schema.")]
        public string? ResponseFormat { get; init; }

        [CommandOption("--presence-penalty <VALUE>")]
        [Description("Presence penalty \u22122..2.")]
        public string? PresencePenalty { get; init; }

        [CommandOption("--frequency-penalty <VALUE>")]
        [Description("Frequency penalty \u22122..2.")]
        public string? FrequencyPenalty { get; init; }
    }

}
