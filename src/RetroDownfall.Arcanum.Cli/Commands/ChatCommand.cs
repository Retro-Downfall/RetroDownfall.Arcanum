using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class ChatCommand(
    IEyeOfTheWorld eye,
    ArcanumApiClient apiClient,
    IOptions<ArcanumSettings> arcanumSettings) : AsyncCommand<ChatCommand.Settings>
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
        "/attach",
    };

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.New)
        {
            CliSessionManager.ClearSession();
        }

        IAnsiConsole stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        AnsiConsole.MarkupLine("[dim]Arcanum chat — [bold]/help[/] for slash commands, [bold]/exit[/] to quit, Ctrl+C to cancel a turn.[/]");

        SessionMut session = new()
        {
            CurrentModel = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim(),
            DisableTools = settings.NoTools,
        };

        HashSet<string> stagedFiles = new(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string raw;

            string promptMarkup = stagedFiles.Count > 0
                ? $"[yellow][[{stagedFiles.Count} file(s) staged]]][/] [bold blue]Mage[/] >"
                : "[bold blue]Mage[/] >";

            try
            {
                raw = AnsiConsole.Prompt(new TextPrompt<string>(promptMarkup).AllowEmpty());
            }
            catch (InvalidOperationException)
            {
                return 0;
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

                string fullPath = Path.GetFullPath(Path.Combine(cwdForAt, tokenPath));

                if (!File.Exists(fullPath))
                {
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
                }

                prompt = prompt.Remove(match.Index, match.Length);
            }

            prompt = prompt.Trim();

            List<AttachedFileDto>? attachedFilesForRequest = null;

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

                            continue;
                        }

                        string contents = File.ReadAllText(file, Encoding.UTF8);

                        string relativePath = Path.GetRelativePath(cwd, file);

                        attached.Add(new AttachedFileDto(relativePath, contents));

                        relativePathsForFooter.Add(relativePath);
                    }
                    catch (IOException ex)
                    {
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(file)}:[/] {Markup.Escape(ex.Message)}");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(file)}:[/] {Markup.Escape(ex.Message)}");
                    }
                }

                if (relativePathsForFooter.Count > 0)
                {
                    prompt += $"\n\n[Attached Files: {string.Join(", ", relativePathsForFooter)}]";

                    attachedFilesForRequest = attached;
                }
            }

            stagedFiles.Clear();

            await RunTurnAsync(
                    prompt,
                    session,
                    settings,
                    stderrConsole,
                    cancellationToken,
                    attachedFilesForRequest)
                .ConfigureAwait(false);
        }

        return 0;
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
            CliSessionManager.ClearSession();

            AnsiConsole.MarkupLine("[green]New conversation thread.[/]");

            return (true, false);
        }

        if (verb.Equals("/model", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(tail))
            {
                AnsiConsole.MarkupLine("[yellow]Usage:[/] [grey]/model <name>[/]");

                return (true, false);
            }

            session.CurrentModel = tail.Trim();

            AnsiConsole.MarkupLine($"[cyan]Model override:[/] {Markup.Escape(session.CurrentModel)}");

            return (true, false);
        }

        if (verb.Equals("/look", StringComparison.OrdinalIgnoreCase))
        {
            PatternSnapshot snapshot = await eye
                .PerceivePatternAsync(Environment.CurrentDirectory, cancellationToken)
                .ConfigureAwait(false);

            PatternSnapshotMarkup.WritePatternSnapshot(snapshot);

            return (true, false);
        }

        if (verb.Equals("/tools", StringComparison.OrdinalIgnoreCase))
        {
            session.DisableTools = !session.DisableTools;

            AnsiConsole.MarkupLine(
                $"[yellow]MCP tools {(session.DisableTools ? "disabled" : "enabled")}.[/]");

            return (true, false);
        }

        if (verb.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            if (!tail.Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[yellow]Usage:[/] [grey]/mcp reload[/]");

                return (true, false);
            }

            PingRequest reloadBody = new(
                Prompt: string.Empty,
                Model: null,
                WorkingDirectory: Environment.CurrentDirectory);

            Result<string> reloadResult = await apiClient.ReloadMcpAsync(reloadBody, cancellationToken).ConfigureAwait(false);

            if (reloadResult.IsSuccess)
            {
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(reloadResult.Value)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(reloadResult.Error.Message)}[/]");
            }

            return (true, false);
        }

        if (verb.Equals("/arsenal", StringComparison.OrdinalIgnoreCase))
        {
            PingRequest arsenalBody = new(
                Prompt: string.Empty,
                Model: null,
                WorkingDirectory: Environment.CurrentDirectory);

            Result<WorkspaceArsenalDto> arsenalResult =
                await apiClient.GetWorkspaceArsenalAsync(arsenalBody, cancellationToken).ConfigureAwait(false);

            if (arsenalResult.IsFailure)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(arsenalResult.Error.Message)}[/]");

                return (true, false);
            }

            RenderArsenalTree(arsenalResult.Value);

            return (true, false);
        }

        if (verb.Equals("/attach", StringComparison.OrdinalIgnoreCase))
        {
            RunAttachBrowser(stagedFiles, Environment.CurrentDirectory, MaxAttachFileSizeBytes);

            return (true, false);
        }

        throw new InvalidOperationException($"Unhandled whitelisted slash verb: {verb}");
    }

    private static void RenderHelp()
    {
        Table table = new();

        table.Border(TableBorder.Rounded);

        table.AddColumn("[grey]Command[/]");

        table.AddColumn("[grey]Description[/]");

        table.AddRow("/exit, /quit", "Leave the REPL.");

        table.AddRow("/clear", "Clear the terminal screen.");

        table.AddRow("/help", "Show this table.");

        table.AddRow("/new", "Clear Grimoire session file; next turn starts a new thread.");

        table.AddRow("/model [cyan]<name>[/]", "Override model for subsequent turns.");

        table.AddRow("/look", "Eye of the World snapshot for the current directory.");

        table.AddRow("/tools", "Toggle MCP tools ([cyan]PingRequest.disableMcpTools[/]).");

        table.AddRow("/mcp reload", "Daemon: dispose MCP partitions, re-bootstrap global [cyan]mcp.json[/].");

        table.AddRow("/arsenal", "Daemon: spells, native tools, and MCP server status.");

        table.AddRow("/attach", "Open interactive file browser to stage files for the next prompt.");

        AnsiConsole.Write(table);
    }

    private static void WriteCannotStageTooLarge(string fileName, long maxAttachFileSizeBytes)
    {
        AnsiConsole.MarkupLine(
            $"[red]Cannot stage {Markup.Escape(fileName)}: File exceeds the configured limit ({maxAttachFileSizeBytes} bytes).[/]");
    }

    private static string FormatBrowseItem(BrowseItem item)
    {
        return item.Kind switch
        {
            BrowseKind.Up => "[blue].. (Up one directory)[/]",
            BrowseKind.Directory => $"[blue]{Markup.Escape(Path.GetFileName(item.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))}/[/]",
            BrowseKind.File => Markup.Escape(Path.GetFileName(item.FullPath)),
            BrowseKind.Cancel => "[red]< Cancel >[/]",
            _ => "?",
        };
    }

    private static void RunAttachBrowser(HashSet<string> stagedFiles, string initialDirectory, long maxAttachFileSizeBytes)
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
                    AnsiConsole.MarkupLine("[red]Access denied; cannot go up.[/]");

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
                .Title($"[cyan]Browsing:[/] {Markup.Escape(currentBrowseDir)}\n[grey](Type to search, Enter to select)[/]")
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
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");

                        continue;
                    }

                    if (len > maxAttachFileSizeBytes)
                    {
                        WriteCannotStageTooLarge(name, maxAttachFileSizeBytes);

                        continue;
                    }

                    stagedFiles.Add(full);

                    AnsiConsole.MarkupLine($"[green]Staged:[/] {Markup.Escape(name)}");

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

    private static void RenderArsenalTree(WorkspaceArsenalDto dto)
    {
        Tree root = new("[bold magenta]Arcanum Arsenal[/]");

        TreeNode spellsNode = root.AddNode("[bold]Spells[/]");

        if (dto.ActiveSpells.Count == 0)
        {
            spellsNode.AddNode("[grey]<none>[/]");
        }
        else
        {
            foreach (string s in dto.ActiveSpells)
            {
                spellsNode.AddNode(Markup.Escape(s));
            }
        }

        TreeNode nativeNode = root.AddNode("[bold]Native Tools[/]");

        if (dto.NativeTools.Count == 0)
        {
            nativeNode.AddNode("[grey]<none>[/]");
        }
        else
        {
            foreach (string n in dto.NativeTools)
            {
                nativeNode.AddNode(Markup.Escape(n));
            }
        }

        TreeNode mcpNode = root.AddNode("[bold]Connected MCP Servers[/]");

        if (dto.McpServers.Count == 0)
        {
            mcpNode.AddNode("[grey]<none>[/]");
        }
        else
        {
            foreach (McpServerStatusDto srv in dto.McpServers)
            {
                string color = string.Equals(srv.Status, "Online", StringComparison.OrdinalIgnoreCase)
                    ? "green"
                    : "red";

                TreeNode srvNode = mcpNode.AddNode(
                    $"[{color}]{Markup.Escape(srv.ServerName)}[/] [grey]({srv.ToolCount} tools)[/]");

                if (!string.IsNullOrWhiteSpace(srv.ErrorMessage))
                {
                    srvNode.AddNode($"[red]{Markup.Escape(srv.ErrorMessage!)}[/]");
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

    private async Task RunTurnAsync(
        string prompt,
        SessionMut session,
        Settings settings,
        IAnsiConsole stderrConsole,
        CancellationToken cancellationToken,
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

        string? finalText = null;

        bool cancelled = false;

        bool errored = false;

        try
        {
            string cwd = Environment.CurrentDirectory;

            PatternSnapshot snapshot = await eye
                .PerceivePatternAsync(cwd, perTurnCts.Token)
                .ConfigureAwait(false);

            Guid? conversationId = CliSessionManager.GetLastConversationId();

            PingRequest ping = new(
                prompt,
                session.CurrentModel,
                cwd,
                snapshot,
                conversationId,
                session.DisableTools,
                CliTerminalFormatting: true,
                UnattendedMode: settings.Unattended,
                AttachedFiles: attachedFiles);

            await foreach (IntelligenceEvent evt in apiClient.AskStreamAsync(ping, perTurnCts.Token).ConfigureAwait(false))
            {
                switch (evt.Type)
                {
                    case IntelligenceEventType.Status:

                        stderrConsole.MarkupLine($"[dim]{Markup.Escape(evt.Message)}[/]");

                        break;

                    case IntelligenceEventType.Token:

                        string chunk = evt.Data ?? string.Empty;

                        if (chunk.Length == 0)
                        {
                            break;
                        }

                        full.Append(chunk);

                        AnsiConsole.Markup(Markup.Escape(chunk));

                        AdvanceLineCounter(chunk, width, ref linesPrinted, ref currentLineLen);

                        break;

                    case IntelligenceEventType.ToolCall:

                        if (await AskHumanToolCallStreamHandler
                                .TryHandleAskHumanAsync(evt, settings.Unattended, apiClient, perTurnCts.Token)
                                .ConfigureAwait(false))
                        {
                            break;
                        }

                        goto case IntelligenceEventType.ToolResult;

                    case IntelligenceEventType.ToolResult:

                        stderrConsole.MarkupLine($"[grey]{Markup.Escape(evt.Data ?? evt.Message)}[/]");

                        break;

                    case IntelligenceEventType.ConversationBound:

                        if (evt.Data is not null && Guid.TryParse(evt.Data, out Guid boundId))
                        {
                            CliSessionManager.SaveConversationId(boundId);
                        }

                        break;

                    case IntelligenceEventType.Result:

                        finalText = evt.Data;

                        break;

                    case IntelligenceEventType.Error:

                        AnsiConsole.WriteLine();

                        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(evt.Message)}");

                        errored = true;

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

            AnsiConsole.MarkupLine("[yellow]<Cancelled>[/]");

            return;
        }

        if (errored)
        {
            return;
        }

        string body = finalText ?? full.ToString();

        if (string.IsNullOrEmpty(body))
        {
            AnsiConsole.WriteLine();

            return;
        }

        if (full.Length > 0)
        {
            if (linesPrinted > 0)
            {
                AnsiConsole.Cursor.Move(CursorDirection.Up, linesPrinted);
            }

            Console.Write("\r\u001b[0J");
        }

        AnsiConsole.Write(MarkdigSpectreRenderer.Render(body));

        AnsiConsole.WriteLine();
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

    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-m|--model")]
        public string? Model { get; init; }

        [CommandOption("-n|--new")]
        [Description("Start a new conversation thread, clearing the previous session at REPL startup.")]
        public bool New { get; init; }

        [CommandOption("--no-tools")]
        [Description("Disable MCP-provided tools for this REPL session (built-in tools still apply).")]
        public bool NoTools { get; init; }

        [CommandOption("--unattended")]
        [Description("Do not block for ask_human; auto-reply so the Mage proceeds without a live operator.")]
        public bool Unattended { get; set; }
    }

}
