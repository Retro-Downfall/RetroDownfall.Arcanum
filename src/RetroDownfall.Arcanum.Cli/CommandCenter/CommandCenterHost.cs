using System.Threading.Channels;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Owns Command Center lifecycle: auto-serve, size gate, input dispatch, chat, exit codes.
/// </summary>
internal sealed class CommandCenterHost(
    IArcanumServeLauncher serveLauncher,
    ICliEnvironment cliEnvironment,
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    ShellCommandDispatcher dispatcher,
    CommandCenterChatRunner chatRunner,
    CommandCenterApp commandCenterApp,
    SessionWorkspaceService sessionWorkspace,
    ILogger<CommandCenterHost> logger) : ICommandCenterHost
{
    public const string NoCommandCenterEnvVar = "ARCANUM_NO_COMMAND_CENTER";

    private static readonly string[] PaletteActions =
    [
        "New Session",
        "Open Sessions",
        "Refresh",
        "Model List",
        "Provider List",
        "MCP Status",
        "Arsenal",
        "Campaign List",
        "Spell List",
        "Ward List",
        "Doctor",
        "Mana",
        "Help",
        "Quit",
    ];

    private PendingConfirm? _pendingConfirm;

    private readonly SemaphoreSlim _actionGate = new(1, 1);

    public static bool IsCommandCenterDisabled()
    {
        string? value = Environment.GetEnvironmentVariable(NoCommandCenterEnvVar);
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        SessionLogBuffer log = new();
        CommandCenterState state = new(log)
        {
            MonochromeTheme = !cliEnvironment.ColorEnabled,
            WorkingDirectory = Environment.CurrentDirectory,
            Model = settingsMonitor.CurrentValue.DefaultModel,
        };

        Channel<CommandCenterUiUpdate> uiChannel = Channel.CreateUnbounded<CommandCenterUiUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        try
        {
            ServeLaunchResult launch = await serveLauncher
                .EnsureRunningAsync(cancellationToken)
                .ConfigureAwait(false);
            state.ServeLaunch = launch;
            state.HealthSummary = launch.Guidance;

            await dispatcher.RefreshMcpAsync(state, cancellationToken).ConfigureAwait(false);
            await sessionWorkspace.RestoreStartupSessionAsync(state, cancellationToken).ConfigureAwait(false);

            int tgCode = commandCenterApp.Run(
                (app, window) =>
            {
                window.WireResize(app);
                window.ApplyState(state, app);
                window.FocusInput(app);
                state.FocusRegion = CommandCenterFocusRegion.Composer;

                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CancellationToken runToken = linked.Token;

                Task pump = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await foreach (CommandCenterUiUpdate update in uiChannel.Reader.ReadAllAsync(runToken)
                                               .ConfigureAwait(false))
                            {
                                app.Invoke(() => window.ApplyState(state, kind: update.Kind));
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    },
                    runToken);

                void SubmitFromInput()
                {
                    // Sole entry into HandleSubmitAsync for composer send (see Accepting no-op below).
                    string text = window.GetComposerText();
                    window.ClearComposer();
                    _ = HandleSubmitAsync(text, state, uiChannel.Writer, app, window, linked);
                }

                void HandleAction(CommandCenterAction action)
                {
                    _ = DispatchActionAsync(action, state, uiChannel.Writer, app, window, linked, SubmitFromInput);
                }

                // ContentsChanged only dirties; ApplyAbsoluteLayout owns frames (UI thread).
                window.SetComposerLayoutRequest(() =>
                {
                    app.Invoke(() => window.ApplyAbsoluteLayout(
                        Math.Max(window.Frame.Width, app.Driver?.Cols ?? 80),
                        Math.Max(window.Frame.Height, app.Driver?.Rows ?? 24)));
                });

                window.Input.KeyDown += (_, e) =>
                {
                    // Logical focus wins: Ctrl+O / Tab may set Sessions while TG focus
                    // still lands on the composer. Route those keys as Sessions — do not
                    // overwrite FocusRegion back to Composer (that made Esc quit and j/k type).
                    CommandCenterFocusRegion routeFocus =
                        state.FocusRegion is CommandCenterFocusRegion.Sessions
                            or CommandCenterFocusRegion.Transcript
                            or CommandCenterFocusRegion.Overlay
                            ? state.FocusRegion
                            : CommandCenterFocusRegion.Composer;

                    if (TryMapAndHandle(
                            e,
                            routeFocus,
                            state,
                            window,
                            HandleAction,
                            syncFocusRegion: routeFocus == CommandCenterFocusRegion.Composer))
                    {
                        return;
                    }

                    if (routeFocus == CommandCenterFocusRegion.Sessions
                        && TryHandleSessionFilterChar(e, state, window, uiChannel.Writer, app))
                    {
                        return;
                    }

                    // Esc must never reach Terminal.Gui's default quit path.
                    if (e == Key.Esc)
                    {
                        e.Handled = true;
                    }
                };

                window.LogView.KeyDown += (_, e) =>
                {
                    _ = TryMapAndHandle(e, CommandCenterFocusRegion.Transcript, state, window, HandleAction);
                };

                window.SessionsView.KeyDown += (_, e) =>
                {
                    if (TryHandleSessionFilterChar(e, state, window, uiChannel.Writer, app))
                    {
                        return;
                    }

                    _ = TryMapAndHandle(e, CommandCenterFocusRegion.Sessions, state, window, HandleAction);
                };

                window.OverlayFilter.KeyDown += (_, e) =>
                {
                    if (e == Key.Esc)
                    {
                        e.Handled = true;
                        CloseOverlay(state, window, app);
                        return;
                    }

                    if (e == Key.Enter)
                    {
                        e.Handled = true;
                        HandleAction(CommandCenterKeymap.MapOverlayEnter(state.Overlay));
                        return;
                    }

                    if (e == Key.CursorUp || e == Key.CursorDown)
                    {
                        e.Handled = true;
                        window.MoveSessionSelection(e == Key.CursorUp ? -1 : 1, state);
                        return;
                    }

                    // Typing filters; refresh after KeyDown so Text is current — schedule refresh.
                    app.Invoke(() =>
                    {
                        state.SessionFilter = window.OverlayFilter.Text?.ToString() ?? string.Empty;
                        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);
                    });
                };

                window.OverlayList.KeyDown += (_, e) =>
                {
                    if (e == Key.Enter)
                    {
                        e.Handled = true;
                        HandleAction(CommandCenterKeymap.MapOverlayEnter(state.Overlay));
                        return;
                    }

                    if (e == Key.Esc
                        && state.Overlay is CommandCenterOverlayKind.QuitConfirm
                            or CommandCenterOverlayKind.DiscardConfirm)
                    {
                        e.Handled = true;
                        CancelPending(state, window, app);
                        return;
                    }

                    _ = TryMapAndHandle(e, CommandCenterFocusRegion.Overlay, state, window, HandleAction);
                };

                // Send ownership: Ctrl+Enter → CommandCenterAction.Send → SubmitFromInput.
                // Bare Enter falls through to TextView (EnterKeyAddsLine=true, required for WordWrap).
                // Accepting is a no-op so it cannot double-submit if raised.
                window.Input.Accepting += (_, e) =>
                {
                    e.Handled = true;
                };

                app.Keyboard.KeyDown += (_, keyEvent) =>
                {
                    KeyChord chord = ToChord(keyEvent);
                    bool globalChord = chord.IsCtrlC || chord.IsCtrlQ || chord.IsCtrlK || chord.IsCtrlO
                        || chord.IsCtrlN || chord.IsCtrlR || chord.IsF1 || chord.IsF5 || chord.IsEsc;

                    // When Sessions is logical focus but the TextField still has TG focus,
                    // intercept nav keys so j/k/Enter never type or send from the composer.
                    // Skip when SessionsView already has focus (its KeyDown owns those chords).
                    bool sessionsNav = state.FocusRegion == CommandCenterFocusRegion.Sessions
                        && !window.IsSessionsFocused
                        && (chord.IsEnter || chord.IsUp || chord.IsDown || chord.IsJ || chord.IsK);

                    if (!globalChord && !sessionsNav)
                    {
                        return;
                    }

                    CommandCenterAction action = CommandCenterKeymap.Map(
                        state.FocusRegion,
                        state.IsStreaming,
                        window.ComposerHasText,
                        state.Overlay != CommandCenterOverlayKind.None,
                        chord);

                    if (action == CommandCenterAction.None && !chord.IsEsc)
                    {
                        return;
                    }

                    keyEvent.Handled = true;
                    if (action is not CommandCenterAction.None and not CommandCenterAction.NoOp)
                    {
                        HandleAction(action);
                    }
                };

                window.FocusInput(app);
                app.Run(window);

                linked.Cancel();
                uiChannel.Writer.TryComplete();
                try
                {
                    pump.GetAwaiter().GetResult();
                }
                catch
                {
                }

                return state.ExitCode;
            },
                state.MonochromeTheme);

            if (tgCode == -2)
            {
                Console.Error.WriteLine(
                    "Terminal too small for Command Center. "
                    + $"Detected {commandCenterApp.LastDetectedCols}x{commandCenterApp.LastDetectedRows}; "
                    + $"need at least {CommandCenterApp.MinCols}x{CommandCenterApp.MinRows}. "
                    + "Resize the terminal, or run a direct command (e.g. `arcanum chat`).");
                return 1;
            }

            if (tgCode == -1)
            {
                Console.Error.WriteLine(
                    "Command Center failed to start. Try `arcanum chat` or another direct command.");
                return 1;
            }

            return state.RequestExit ? state.ExitCode : tgCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command Center host failed.");
            Console.Error.WriteLine($"Command Center error: {ex.Message}");
            return 1;
        }
        finally
        {
            _actionGate.Dispose();
        }
    }

    private async Task RunGatedAsync(
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> ui,
        CancellationToken cancellationToken,
        Func<Task> core)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await core().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.TransientStatus = null;
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command Center gated action failed.");
            state.TransientStatus = null;
            state.FooterHint = string.IsNullOrWhiteSpace(ex.Message)
                ? "Action failed."
                : ex.Message;
            state.LastError = state.FooterHint;
            try
            {
                await ui.WriteAsync(
                        new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Channel may be completed on exit.
            }
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task DispatchActionAsync(
        CommandCenterAction action,
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> ui,
        IApplication app,
        CommandCenterWindow window,
        CancellationTokenSource linked,
        Action submitFromInput)
    {
        switch (action)
        {
            case CommandCenterAction.NoOp:
                break;

            case CommandCenterAction.Send:
                submitFromInput();
                break;

            case CommandCenterAction.InsertComposerNewLine:
                app.Invoke(() =>
                {
                    window.InsertComposerNewLine();
                    window.FocusInput();
                });
                break;

            case CommandCenterAction.CancelTurn:
                // Cancel only — Host owns TurnCts disposal.
                state.TurnCts?.Cancel();
                break;

            case CommandCenterAction.ClearComposer:
                app.Invoke(() =>
                {
                    window.ClearComposer();
                    window.FocusInput();
                    state.FocusRegion = CommandCenterFocusRegion.Composer;
                    state.FooterHint = null;
                    window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                });
                break;

            case CommandCenterAction.QuitHint:
                state.FooterHint = "Press Ctrl+Q to quit";
                await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshFooter), linked.Token)
                    .ConfigureAwait(false);
                break;

            case CommandCenterAction.Quit:
                RequestQuit(state, window, app);
                break;

            case CommandCenterAction.Help:
                ShowHelpOverlay(state, window, app);
                break;

            case CommandCenterAction.CommandPalette:
                ShowPalette(state, window, app);
                break;

            case CommandCenterAction.FocusSessions:
                state.FocusRegion = CommandCenterFocusRegion.Sessions;
                state.FooterHint = null;
                // Prefer the left Sessions pane when visible (Claude Code–style).
                // Only open the overlay picker when the sidebar is collapsed (<100 cols).
                app.Invoke(() =>
                {
                    window.FocusSessions(forceOverlay: !window.SidebarVisible);
                    if (!window.SidebarVisible)
                    {
                        state.Overlay = CommandCenterOverlayKind.SessionPicker;
                        state.SessionFilter = string.Empty;
                        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);
                    }
                    else
                    {
                        state.Overlay = CommandCenterOverlayKind.None;
                        window.EnsureSessionSelection(state);
                    }

                    window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                });
                break;

            case CommandCenterAction.NewSession:
                await RunGatedAsync(
                        state,
                        ui,
                        linked.Token,
                        () => RequestNewSessionCoreAsync(state, window, app, ui, linked.Token))
                    .ConfigureAwait(false);
                break;

            case CommandCenterAction.Refresh:
                await RunGatedAsync(
                        state,
                        ui,
                        linked.Token,
                        () => RefreshSessionsCoreAsync(state, ui, linked.Token))
                    .ConfigureAwait(false);
                break;

            case CommandCenterAction.CycleFocusNext:
                CycleFocus(state, window, app, forward: true);
                break;

            case CommandCenterAction.CycleFocusPrev:
                CycleFocus(state, window, app, forward: false);
                break;

            case CommandCenterAction.CloseOverlayOrFocusComposer:
                CloseOverlay(state, window, app);
                break;

            case CommandCenterAction.SessionSelectUp:
                window.MoveSessionSelection(-1, state);
                break;

            case CommandCenterAction.SessionSelectDown:
                window.MoveSessionSelection(1, state);
                break;

            case CommandCenterAction.ResumeSelectedSession:
                await RunGatedAsync(
                        state,
                        ui,
                        linked.Token,
                        () => ResumeSelectedCoreAsync(state, window, app, ui, linked.Token))
                    .ConfigureAwait(false);
                break;

            case CommandCenterAction.ExecutePaletteItem:
            {
                int idx = window.GetOverlaySelectedIndex();
                CloseOverlay(state, window, app);
                if (idx >= 0 && idx < PaletteActions.Length)
                {
                    await RunPaletteActionAsync(
                            PaletteActions[idx],
                            state,
                            ui,
                            app,
                            window,
                            linked)
                        .ConfigureAwait(false);
                }

                break;
            }

            case CommandCenterAction.ConfirmPending:
                await ConfirmPendingAsync(state, window, app, ui, linked).ConfigureAwait(false);
                break;

            case CommandCenterAction.ScrollTranscriptUp:
                window.ScrollLogUp();
                break;

            case CommandCenterAction.ScrollTranscriptDown:
                window.ScrollLogDown();
                break;

            case CommandCenterAction.PageTranscriptUp:
                if (state.FocusRegion == CommandCenterFocusRegion.Composer)
                {
                    state.FocusRegion = CommandCenterFocusRegion.Transcript;
                    window.FocusLog();
                }

                window.PageLogUp();
                break;

            case CommandCenterAction.PageTranscriptDown:
                window.PageLogDown();
                break;

            case CommandCenterAction.JumpTranscriptHome:
                window.ScrollLogHome();
                break;

            case CommandCenterAction.JumpTranscriptEnd:
                window.ScrollLogEnd();
                break;
        }
    }

    private async Task RefreshSessionsCoreAsync(
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> ui,
        CancellationToken cancellationToken)
    {
        state.TransientStatus = "Refreshing sessions…";
        await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshHeader), cancellationToken)
            .ConfigureAwait(false);
        await sessionWorkspace.RefreshSessionsAsync(state, cancellationToken).ConfigureAwait(false);
        await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ResumeSelectedCoreAsync(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        ChannelWriter<CommandCenterUiUpdate> ui,
        CancellationToken cancellationToken)
    {
        if (CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? deny))
        {
            await ui.WriteAsync(deny!, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Transcript Guid fallback (legacy log pick).
        if (state.FocusRegion == CommandCenterFocusRegion.Transcript
            && state.Overlay == CommandCenterOverlayKind.None)
        {
            int index = window.GetSelectedLogIndex();
            if (SessionIdLineParser.TryExtractNear(window.GetLogLinesSnapshot(), index, out Guid logId))
            {
                await ResumeWithConfirmCoreAsync(state, window, app, ui, logId, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        Guid? id = window.GetSelectedSessionId(state);
        if (id is null)
        {
            return;
        }

        await ResumeWithConfirmCoreAsync(state, window, app, ui, id.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResumeWithConfirmCoreAsync(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        ChannelWriter<CommandCenterUiUpdate> ui,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? deny))
        {
            await ui.WriteAsync(deny!, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (window.ComposerHasText)
        {
            _pendingConfirm = new PendingConfirm(PendingConfirmKind.ResumeSession, sessionId);
            ShowDiscardConfirm(state, window, app);
            return;
        }

        await DoResumeCoreAsync(state, window, app, ui, sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task DoResumeCoreAsync(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        ChannelWriter<CommandCenterUiUpdate> ui,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? deny))
        {
            await ui.WriteAsync(deny!, cancellationToken).ConfigureAwait(false);
            return;
        }

        CloseOverlay(state, window, app);
        state.TransientStatus = "Loading session…";
        await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshHeader), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            SessionResumeResult result = await sessionWorkspace
                .ResumeSessionAsync(state, sessionId, cancellationToken)
                .ConfigureAwait(false);

            await sessionWorkspace.RefreshSessionsAsync(state, cancellationToken).ConfigureAwait(false);
            await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll), cancellationToken)
                .ConfigureAwait(false);

            app.Invoke(() =>
            {
                if (result.Outcome == SessionResumeOutcome.Success)
                {
                    window.ApplyState(state, forceFollowTail: true);
                }

                window.FocusInput();
                state.FocusRegion = CommandCenterFocusRegion.Composer;
            });
        }
        finally
        {
            if (state.TransientStatus is not null)
            {
                state.TransientStatus = null;
                await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshHeader), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RequestNewSessionCoreAsync(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        ChannelWriter<CommandCenterUiUpdate> ui,
        CancellationToken cancellationToken)
    {
        if (CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? deny))
        {
            await ui.WriteAsync(deny!, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (window.ComposerHasText)
        {
            _pendingConfirm = new PendingConfirm(PendingConfirmKind.NewSession, null);
            ShowDiscardConfirm(state, window, app);
            return;
        }

        sessionWorkspace.StartNewSession(state);
        await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll), cancellationToken)
            .ConfigureAwait(false);
        app.Invoke(() =>
        {
            window.ApplyState(state, forceFollowTail: true);
            window.FocusInput();
            state.FocusRegion = CommandCenterFocusRegion.Composer;
        });
    }

    private async Task ConfirmPendingAsync(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        ChannelWriter<CommandCenterUiUpdate> ui,
        CancellationTokenSource linked)
    {
        PendingConfirm? pending = _pendingConfirm;
        _pendingConfirm = null;
        CloseOverlay(state, window, app);
        if (pending is null)
        {
            return;
        }

        window.ClearComposer();
        if (pending.Kind == PendingConfirmKind.Quit)
        {
            state.RequestExit = true;
            state.ExitCode = 0;
            app.Invoke(() => app.RequestStop());
            return;
        }

        if (pending.Kind == PendingConfirmKind.NewSession)
        {
            await RunGatedAsync(
                    state,
                    ui,
                    linked.Token,
                    async () =>
                    {
                        if (CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(
                                state,
                                out CommandCenterUiUpdate? deny))
                        {
                            await ui.WriteAsync(deny!, linked.Token).ConfigureAwait(false);
                            return;
                        }

                        sessionWorkspace.StartNewSession(state);
                        await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll), linked.Token)
                            .ConfigureAwait(false);
                        app.Invoke(() =>
                        {
                            window.ApplyState(state, forceFollowTail: true);
                            window.FocusInput();
                        });
                    })
                .ConfigureAwait(false);
            return;
        }

        if (pending.Kind == PendingConfirmKind.ResumeSession && pending.SessionId is { } id)
        {
            await RunGatedAsync(
                    state,
                    ui,
                    linked.Token,
                    () => DoResumeCoreAsync(state, window, app, ui, id, linked.Token))
                .ConfigureAwait(false);
        }
    }

    private void CancelPending(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        _pendingConfirm = null;
        CloseOverlay(state, window, app);
    }

    private void RequestQuit(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        if (state.Generating)
        {
            _pendingConfirm = new PendingConfirm(PendingConfirmKind.Quit, null);
            state.Overlay = CommandCenterOverlayKind.QuitConfirm;
            app.Invoke(() =>
            {
                window.ShowOverlay(
                    CommandCenterOverlayKind.QuitConfirm,
                    ["A turn is still generating.", "Enter = quit anyway", "Esc = cancel"],
                    "Quit?",
                    showFilter: false);
            });
            return;
        }

        state.RequestExit = true;
        state.ExitCode = 0;
        app.Invoke(() => app.RequestStop());
    }

    private static void ShowDiscardConfirm(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        state.Overlay = CommandCenterOverlayKind.DiscardConfirm;
        app.Invoke(() =>
        {
            window.ShowOverlay(
                CommandCenterOverlayKind.DiscardConfirm,
                ["Discard unsent composer text?", "Enter = discard", "Esc = cancel"],
                "Discard?",
                showFilter: false);
        });
    }

    private static void ShowHelpOverlay(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        state.Overlay = CommandCenterOverlayKind.Help;
        app.Invoke(() =>
        {
            window.ShowOverlay(
                CommandCenterOverlayKind.Help,
                [
                    "F1 Help",
                    "Ctrl+K Command palette",
                    "Ctrl+O Sessions",
                    "Ctrl+N New session",
                    "Ctrl+R / F5 Refresh",
                    "Tab / Shift+Tab Cycle focus",
                    "Ctrl+Enter Send (composer)",
                    "Enter Newline (composer)",
                    "Enter Resume (sessions)",
                    "Ctrl+C Cancel turn / clear input / quit hint",
                    "Ctrl+Q Quit",
                    "Esc Close overlay / focus composer",
                    "PgUp/PgDn Transcript scroll",
                    "",
                    "Slash: /help /keys /session list|new|resume",
                    "Denied: /serve /daemon… /key…",
                ],
                "Help",
                showFilter: false);
        });
    }

    private static void ShowPalette(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        state.Overlay = CommandCenterOverlayKind.CommandPalette;
        app.Invoke(() =>
        {
            window.ShowOverlay(
                CommandCenterOverlayKind.CommandPalette,
                PaletteActions,
                "Commands",
                showFilter: false);
        });
    }

    private async Task RunPaletteActionAsync(
        string action,
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> ui,
        IApplication app,
        CommandCenterWindow window,
        CancellationTokenSource linked)
    {
        switch (action)
        {
            case "New Session":
                await RunGatedAsync(
                        state,
                        ui,
                        linked.Token,
                        () => RequestNewSessionCoreAsync(state, window, app, ui, linked.Token))
                    .ConfigureAwait(false);
                break;
            case "Open Sessions":
                await DispatchActionAsync(
                        CommandCenterAction.FocusSessions,
                        state,
                        ui,
                        app,
                        window,
                        linked,
                        static () => { })
                    .ConfigureAwait(false);
                break;
            case "Refresh":
                await RunGatedAsync(
                        state,
                        ui,
                        linked.Token,
                        () => RefreshSessionsCoreAsync(state, ui, linked.Token))
                    .ConfigureAwait(false);
                break;
            case "Quit":
                RequestQuit(state, window, app);
                break;
            case "Help":
                ShowHelpOverlay(state, window, app);
                break;
            case "Model List":
                await DispatchSlashAsync("/model list", state, ui, app, window, linked).ConfigureAwait(false);
                break;
            case "Provider List":
                await DispatchSlashAsync("/provider list", state, ui, app, window, linked).ConfigureAwait(false);
                break;
            case "MCP Status":
                await DispatchSlashAsync("/mcp", state, ui, app, window, linked).ConfigureAwait(false);
                break;
            case "Arsenal":
                await DispatchSlashAsync("/arsenal", state, ui, app, window, linked).ConfigureAwait(false);
                break;
            case "Campaign List":
                await DispatchSlashAsync("/campaign list", state, ui, app, window, linked).ConfigureAwait(false);
                break;
            case "Spell List":
                await DispatchSlashAsync("/spell list", state, ui, app, window, linked).ConfigureAwait(false);
                break;
            case "Ward List":
                await DispatchSlashAsync("/ward list", state, ui, app, window, linked).ConfigureAwait(false);
                break;
            case "Doctor":
                await DispatchSlashAsync("/doctor", state, ui, app, window, linked).ConfigureAwait(false);
                break;
            case "Mana":
                await DispatchSlashAsync("/mana", state, ui, app, window, linked).ConfigureAwait(false);
                break;
        }
    }

    private async Task DispatchSlashAsync(
        string slash,
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> ui,
        IApplication app,
        CommandCenterWindow window,
        CancellationTokenSource linked)
    {
        await RunGatedAsync(
                state,
                ui,
                linked.Token,
                async () =>
                {
                    ShellDispatchResult result = await dispatcher
                        .DispatchAsync(slash, state, linked.Token)
                        .ConfigureAwait(false);
                    await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll), linked.Token)
                        .ConfigureAwait(false);
                    if (result == ShellDispatchResult.Exit)
                    {
                        app.Invoke(() => app.RequestStop());
                    }
                    else
                    {
                        app.Invoke(() =>
                        {
                            window.ApplyState(state, forceFollowTail: true);
                            window.FocusInput();
                        });
                    }
                })
            .ConfigureAwait(false);
    }

    private static void CloseOverlay(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        state.Overlay = CommandCenterOverlayKind.None;
        state.SessionFilter = string.Empty;
        state.FooterHint = null;
        app.Invoke(() =>
        {
            window.HideOverlayVisual();
            window.FocusInput();
            state.FocusRegion = CommandCenterFocusRegion.Composer;
            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
    }

    private static void CycleFocus(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        bool forward)
    {
        CommandCenterFocusRegion[] order = window.SidebarVisible
            ? [CommandCenterFocusRegion.Composer, CommandCenterFocusRegion.Sessions, CommandCenterFocusRegion.Transcript]
            : [CommandCenterFocusRegion.Composer, CommandCenterFocusRegion.Transcript];

        int idx = Array.IndexOf(order, state.FocusRegion);
        if (idx < 0)
        {
            idx = 0;
        }

        idx = forward ? (idx + 1) % order.Length : (idx - 1 + order.Length) % order.Length;
        state.FocusRegion = order[idx];
        state.FooterHint = null;
        app.Invoke(() =>
        {
            switch (state.FocusRegion)
            {
                case CommandCenterFocusRegion.Sessions:
                    window.FocusSessions();
                    break;
                case CommandCenterFocusRegion.Transcript:
                    window.FocusLog();
                    break;
                default:
                    window.FocusInput();
                    break;
            }

            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
    }

    private async Task HandleSubmitAsync(
        string text,
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> ui,
        IApplication app,
        CommandCenterWindow window,
        CancellationTokenSource linked)
    {
        if (!CommandCenterSubmitText.TryPrepare(text, out string payload, out bool isSlash))
        {
            return;
        }

        state.FooterHint = null;

        if (isSlash)
        {
            await RunGatedAsync(
                    state,
                    ui,
                    linked.Token,
                    async () =>
                    {
                        ShellDispatchResult result = await dispatcher
                            .DispatchAsync(payload, state, linked.Token)
                            .ConfigureAwait(false);

                        await ui.WriteAsync(
                                new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll),
                                linked.Token)
                            .ConfigureAwait(false);

                        if (result == ShellDispatchResult.Exit)
                        {
                            app.Invoke(() => app.RequestStop());
                            return;
                        }

                        app.Invoke(() =>
                        {
                            window.ApplyState(state, forceFollowTail: true);
                            window.FocusInput();
                        });
                    })
                .ConfigureAwait(false);
            return;
        }

        if (!state.TryBeginTurn())
        {
            state.Log.Append(SessionLogEntryKind.Status, "Already generating — Ctrl+C to cancel.");
            await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog), linked.Token)
                .ConfigureAwait(false);
            return;
        }

        CancellationTokenSource? turnCts = null;
        try
        {
            turnCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            state.TurnCts = turnCts;
            await chatRunner
                .RunTurnAsync(payload, state, ui, turnCts.Token)
                .ConfigureAwait(false);

            await sessionWorkspace.RefreshSessionsAsync(state, linked.Token).ConfigureAwait(false);
            await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll), linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command Center submit failed.");
            state.Log.Append(SessionLogEntryKind.Error, ex.Message);
            await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            state.EndTurn();

            // Host owns TurnCts: capture → null → dispose. Ctrl+C / ChatRunner never dispose.
            CancellationTokenSource? captured = state.TurnCts;
            state.TurnCts = null;
            captured?.Dispose();
            if (turnCts is not null && !ReferenceEquals(turnCts, captured))
            {
                turnCts.Dispose();
            }

            app.Invoke(() =>
            {
                window.ApplyState(state, forceFollowTail: true);
                window.FocusInput();
                state.FocusRegion = CommandCenterFocusRegion.Composer;
            });
        }
    }

    private static bool TryMapAndHandle(
        Key e,
        CommandCenterFocusRegion focus,
        CommandCenterState state,
        CommandCenterWindow window,
        Action<CommandCenterAction> handle,
        bool syncFocusRegion = true)
    {
        if (syncFocusRegion)
        {
            state.FocusRegion = focus;
        }

        KeyChord chord = ToChord(e);
        CommandCenterAction action = CommandCenterKeymap.Map(
            focus,
            state.IsStreaming,
            window.ComposerHasText,
            state.Overlay != CommandCenterOverlayKind.None || window.OverlayPane.Visible,
            chord);

        if (action == CommandCenterAction.None)
        {
            return false;
        }

        e.Handled = true;
        if (action != CommandCenterAction.NoOp)
        {
            handle(action);
        }

        return true;
    }

    private static bool TryHandleSessionFilterChar(
        Key e,
        CommandCenterState state,
        CommandCenterWindow window,
        ChannelWriter<CommandCenterUiUpdate> ui,
        IApplication app)
    {
        // Sidebar filter: printable characters append to SessionFilter when sessions focused.
        if (e.IsCtrl || e.IsAlt || e == Key.Enter || e == Key.Esc || e == Key.Tab
            || e == Key.CursorUp || e == Key.CursorDown || e == Key.PageUp || e == Key.PageDown
            || e == Key.Home || e == Key.End || e == Key.Backspace)
        {
            if (e == Key.Backspace && state.SessionFilter.Length > 0)
            {
                e.Handled = true;
                state.SessionFilter = state.SessionFilter[..^1];
                app.Invoke(() => window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar));
                return true;
            }

            return false;
        }

        char? ch = TryGetChar(e);
        if (ch is null || char.IsControl(ch.Value))
        {
            return false;
        }

        // j/k navigation takes precedence via keymap when bare.
        if ((ch == 'j' || ch == 'k') && string.IsNullOrEmpty(state.SessionFilter))
        {
            return false;
        }

        e.Handled = true;
        state.SessionFilter += ch.Value;
        app.Invoke(() => window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar));
        return true;
    }

    private static char? TryGetChar(Key key)
    {
        try
        {
            if (key.TryGetPrintableRune(out System.Text.Rune rune) && rune.IsAscii && !Rune.IsControl(rune))
            {
                return (char)rune.Value;
            }

            string grapheme = key.AsGrapheme;
            if (!string.IsNullOrEmpty(grapheme) && grapheme.Length == 1)
            {
                return grapheme[0];
            }
        }
        catch
        {
        }

        return null;
    }

    private static KeyChord ToChord(Key key)
    {
        bool ctrl = key.IsCtrl;
        char? ch = TryGetChar(key);
        bool isLetter = ch is { } c && char.IsLetter(c);
        // Ctrl/Shift/Alt+Enter may arrive as WithCtrl/WithShift/WithAlt rather than bare Key.Enter.
        bool isEnter = key == Key.Enter
            || key == Key.Enter.WithShift
            || key == Key.Enter.WithAlt
            || key == Key.Enter.WithCtrl
            || (key.KeyCode & ~(KeyCode.ShiftMask | KeyCode.AltMask | KeyCode.CtrlMask)) == KeyCode.Enter;
        return new KeyChord(
            IsEnter: isEnter,
            IsEsc: key == Key.Esc,
            IsTab: key == Key.Tab || key == Key.Tab.WithShift,
            IsShift: key.IsShift,
            IsAlt: key.IsAlt,
            IsCtrl: key.IsCtrl,
            IsCtrlC: key == Key.C.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.C),
            IsCtrlK: key == Key.K.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.K),
            IsCtrlO: key == Key.O.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.O),
            IsCtrlN: key == Key.N.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.N),
            IsCtrlR: key == Key.R.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.R),
            IsCtrlQ: key == Key.Q.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.Q),
            IsF1: key == Key.F1,
            IsF5: key == Key.F5,
            IsUp: key == Key.CursorUp,
            IsDown: key == Key.CursorDown,
            IsPageUp: key == Key.PageUp,
            IsPageDown: key == Key.PageDown,
            IsHome: key == Key.Home,
            IsEnd: key == Key.End,
            IsJ: !ctrl && ch is 'j' or 'J',
            IsK: !ctrl && ch is 'k' or 'K',
            IsBareLetter: !ctrl && !key.IsAlt && isLetter);
    }

    private enum PendingConfirmKind
    {
        Quit,
        NewSession,
        ResumeSession,
    }

    private sealed record PendingConfirm(PendingConfirmKind Kind, Guid? SessionId);
}
