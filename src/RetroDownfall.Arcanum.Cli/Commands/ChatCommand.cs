using System.ComponentModel;
using System.Text;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class ChatCommand(IEyeOfTheWorld eye, ArcanumApiClient apiClient) : AsyncCommand<ChatCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.New)
        {
            CliSessionManager.ClearSession();
        }

        IAnsiConsole stderrConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

        AnsiConsole.MarkupLine("[dim]Arcanum chat — type [bold]/exit[/] to quit, [bold]/clear[/] to clear the screen, Ctrl+C to cancel a turn.[/]");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string raw;

            try
            {
                raw = AnsiConsole.Prompt(new TextPrompt<string>("[bold blue]Mage[/] >").AllowEmpty());
            }
            catch (InvalidOperationException)
            {
                return 0;
            }

            string prompt = raw.Trim();

            if (prompt.Length == 0)
            {
                continue;
            }

            if (string.Equals(prompt, "/exit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prompt, "/quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.Equals(prompt, "/clear", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.Clear();

                continue;
            }

            await RunTurnAsync(prompt, settings, stderrConsole, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task RunTurnAsync(string prompt, Settings settings, IAnsiConsole stderrConsole, CancellationToken cancellationToken)
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
                string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim(),
                cwd,
                snapshot,
                conversationId,
                settings.NoTools,
                CliTerminalFormatting: true,
                UnattendedMode: settings.Unattended);

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
