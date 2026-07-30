using System.Threading.Channels;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Owns Command Center lifecycle: auto-serve, size gate, input dispatch, chat, exit codes.
/// </summary>
internal sealed class CommandCenterHost(
    IArcanumServeLauncher serveLauncher,
    ArcanumApiClient apiClient,
    ICliEnvironment cliEnvironment,
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    ShellCommandDispatcher dispatcher,
    CommandCenterChatRunner chatRunner,
    CommandCenterApp commandCenterApp,
    SessionWorkspaceService sessionWorkspace,
    CommandCenterWardCoordinator wardCoordinator,
    CommandCenterHardModalArbiter hardModalArbiter,
    CommandCenterHumanPromptCoordinator humanPromptCoordinator,
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

    private System.Threading.Timer? _thinkingTimer;

    private int _thinkingCallbackQueued;

    /// <summary>Prevents Tab from cycling twice when both focused-view and app.Keyboard fire.</summary>
    private long _lastTabHandledTicks;

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

                wardCoordinator.SetUiCallbacks(
                    onShow: request =>
                    {
                        app.Invoke(() =>
                        {
                            state.Overlay = CommandCenterOverlayKind.WardConfirm;
                            state.FocusRegion = CommandCenterFocusRegion.Overlay;
                            List<string> lines =
                            [
                                $"Forbidden art requires approval: {request.ToolName}",
                                $"Ward: {request.WardId}",
                            ];
                            if (!string.IsNullOrWhiteSpace(request.ArgumentsPreview))
                            {
                                lines.Add(request.ArgumentsPreview!);
                            }

                            lines.AddRange(WardOverlayContent.ChoiceLines);

                            window.ShowOverlay(
                                CommandCenterOverlayKind.WardConfirm,
                                lines,
                                "Ward",
                                showFilter: false);
                            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                        });
                    },
                    onHide: () =>
                    {
                        app.Invoke(() =>
                        {
                            if (state.Overlay == CommandCenterOverlayKind.WardConfirm)
                            {
                                CloseOverlay(state, window, app);
                            }
                        });
                    });

                humanPromptCoordinator.SetUiCallbacks(
                    onShow: (request, status) =>
                    {
                        app.Invoke(() =>
                        {
                            // Hard modals are never preempted — arbiter only invokes show when this
                            // HumanPrompt is the active slot (queued wards keep observing timeout).
                            state.Overlay = CommandCenterOverlayKind.HumanPrompt;
                            state.FocusRegion = CommandCenterFocusRegion.Overlay;
                            state.FooterHint = status;
                            window.ShowHumanPromptOverlay(request.Question, request.PromptId, status);
                            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                        });
                    },
                    onHide: (reason, notice) =>
                    {
                        app.Invoke(() =>
                        {
                            // Stale closes are filtered by PromptId in the coordinator before onHide.
                            if (state.Overlay == CommandCenterOverlayKind.HumanPrompt)
                            {
                                CloseOverlayAndFocusInput(state, window, app);
                            }

                            if (!string.IsNullOrWhiteSpace(notice))
                            {
                                state.FooterHint = notice;
                                state.Log.Append(SessionLogEntryKind.Status, notice!);
                                window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshAll);
                            }
                        });
                    },
                    onStatus: status =>
                    {
                        app.Invoke(() =>
                        {
                            if (state.Overlay != CommandCenterOverlayKind.HumanPrompt)
                            {
                                return;
                            }

                            HumanPromptRequest? pending = humanPromptCoordinator.Pending;
                            if (pending is null)
                            {
                                return;
                            }

                            state.FooterHint = status;
                            // Refresh body with status while preserving answer TextView contents.
                            string answer = window.GetHumanPromptAnswer();
                            window.ShowHumanPromptOverlay(pending.Question, pending.PromptId, status);
                            if (!string.IsNullOrEmpty(answer))
                            {
                                try
                                {
                                    window.OverlayAnswer.Text = answer;
                                }
                                catch
                                {
                                }
                            }

                            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                        });
                    });

                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CancellationToken runToken = linked.Token;

                Task pump = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await foreach (CommandCenterUiUpdate update in uiChannel.Reader.ReadAllAsync(runToken).ConfigureAwait(false))
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
                    // ClearComposer runs only after TryBeginTurn admits the turn (#10).
                    string text = window.GetComposerText();

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

                // Single Tab path: handle on the focused view (mark Handled so TextView cannot
                // insert \t / TG AdvanceFocus cannot steal). app.Keyboard is a fallback only.
                void HandleTabChord(Key e)
                {
                    e.Handled = true;
                    long now = Environment.TickCount64;
                    if (now - _lastTabHandledTicks < 40)
                    {
                        return;
                    }

                    _lastTabHandledTicks = now;
                    bool overlayOpen = state.Overlay != CommandCenterOverlayKind.None;
                    CommandCenterAction tabAction = CommandCenterKeymap.Map(
                        state.FocusRegion,
                        state.IsStreaming,
                        window.ComposerHasText,
                        overlayOpen,
                        ToChord(e));
                    if (tabAction is CommandCenterAction.CycleFocusNext or CommandCenterAction.CycleFocusPrev)
                    {
                        HandleAction(tabAction);
                    }
                }

                window.Input.KeyDown += (_, e) =>
                {
                    // Logical focus wins: Ctrl+O / Tab may set Sessions while TG focus
                    // still lands on the composer. Route those keys as Sessions — do not
                    // overwrite FocusRegion back to Composer (that made Esc quit and j/k type).
                    CommandCenterFocusRegion routeFocus =
                        state.FocusRegion is CommandCenterFocusRegion.Sessions
                            or CommandCenterFocusRegion.Transcript
                            or CommandCenterFocusRegion.Incantations
                            or CommandCenterFocusRegion.Overlay
                            ? state.FocusRegion
                            : CommandCenterFocusRegion.Composer;

                    if (e == Key.Tab || e == Key.Tab.WithShift)
                    {
                        HandleTabChord(e);
                        return;
                    }

                    // Modal overlays often fail to steal TG focus from TextView; handle keys here.
                    if (TryHandleModalOverlayKey(e, state, window, app, HandleAction))
                    {
                        return;
                    }

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
                    if (e == Key.Tab || e == Key.Tab.WithShift)
                    {
                        HandleTabChord(e);
                        return;
                    }

                    _ = TryMapAndHandle(e, CommandCenterFocusRegion.Transcript, state, window, HandleAction);
                };

                window.IncantationsView.KeyDown += (_, e) =>
                {
                    if (e == Key.Tab || e == Key.Tab.WithShift)
                    {
                        HandleTabChord(e);
                        return;
                    }

                    _ = TryMapAndHandle(e, CommandCenterFocusRegion.Incantations, state, window, HandleAction);
                };

                window.SessionsView.KeyDown += (_, e) =>
                {
                    if (e == Key.Tab || e == Key.Tab.WithShift)
                    {
                        HandleTabChord(e);
                        return;
                    }

                    if (TryHandleSessionFilterChar(e, state, window, uiChannel.Writer, app))
                    {
                        return;
                    }

                    _ = TryMapAndHandle(e, CommandCenterFocusRegion.Sessions, state, window, HandleAction);
                };

                window.OverlayFilter.KeyDown += (_, e) =>
                {
                    if (e == Key.Tab || e == Key.Tab.WithShift)
                    {
                        HandleTabChord(e);
                        return;
                    }

                    if (e == Key.Esc)
                    {
                        e.Handled = true;
                        CloseOverlayAndFocusInput(state, window, app);
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

                void OnOverlayKeyDown(object? _, Key e)
                {
                    if (e == Key.Tab || e == Key.Tab.WithShift)
                    {
                        HandleTabChord(e);
                        return;
                    }

                    if (e == Key.Enter)
                    {
                        if (state.Overlay == CommandCenterOverlayKind.HumanPrompt)
                        {
                            // Enter = newline in answer field; handled by OverlayAnswer path.
                            return;
                        }

                        e.Handled = true;
                        HandleAction(CommandCenterKeymap.MapOverlayEnter(state.Overlay));
                        return;
                    }

                    if (e == Key.Esc
                        && state.Overlay == CommandCenterOverlayKind.HumanPrompt)
                    {
                        e.Handled = true;
                        state.FooterHint = "Ctrl+Enter to submit · Ctrl+C to cancel turn";
                        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                        return;
                    }

                    if (e == Key.Esc
                        && state.Overlay is CommandCenterOverlayKind.QuitConfirm
                            or CommandCenterOverlayKind.DiscardConfirm
                            or CommandCenterOverlayKind.WardConfirm)
                    {
                        e.Handled = true;
                        CancelPending(state, window, app);
                        return;
                    }

                    if (state.Overlay == CommandCenterOverlayKind.WardConfirm
                        && (e == Key.A || e == Key.A.WithShift))
                    {
                        e.Handled = true;
                        _ = wardCoordinator.TryCompletePending(WardApprovalDecision.AllowAlwaysThisTool);
                        CloseOverlayAndFocusInput(state, window, app);
                        return;
                    }

                    if (state.Overlay == CommandCenterOverlayKind.WardConfirm
                        && (e == Key.O || e == Key.O.WithShift))
                    {
                        e.Handled = true;
                        _ = wardCoordinator.TryCompletePending(WardApprovalDecision.Allow);
                        CloseOverlayAndFocusInput(state, window, app);
                        return;
                    }

                    if (state.Overlay == CommandCenterOverlayKind.WardConfirm
                        && (e == Key.D || e == Key.D.WithShift))
                    {
                        e.Handled = true;
                        _ = wardCoordinator.TryCompletePending(WardApprovalDecision.Deny);
                        CloseOverlayAndFocusInput(state, window, app);
                        return;
                    }

                    _ = TryMapAndHandle(e, CommandCenterFocusRegion.Overlay, state, window, HandleAction);
                }

                window.OverlayList.KeyDown += OnOverlayKeyDown;
                window.OverlayBody.KeyDown += OnOverlayKeyDown;
                window.OverlayAnswer.KeyDown += (_, e) =>
                {
                    if (state.Overlay != CommandCenterOverlayKind.HumanPrompt)
                    {
                        return;
                    }

                    if (e == Key.Tab || e == Key.Tab.WithShift)
                    {
                        e.Handled = true;
                        return;
                    }

                    KeyChord chord = ToChord(e);
                    if (chord.IsEnter && chord.IsCtrl)
                    {
                        e.Handled = true;
                        CancellationToken submitToken = state.TurnCts?.Token ?? linked.Token;
                        _ = SubmitHumanPromptAsync(state, window, app, uiChannel.Writer, submitToken);
                        return;
                    }

                    if (e == Key.Esc)
                    {
                        // Hard modal: Esc does not dismiss; Ctrl+C cancels the turn.
                        e.Handled = true;
                        state.FooterHint = "Ctrl+Enter to submit · Ctrl+C to cancel turn";
                        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                        return;
                    }

                    // Bare Enter = newline (TextView default). Do not ConfirmPending.
                    if (chord.IsEnter && !chord.IsCtrl)
                    {
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

                    // Fallback Tab path when no focused child KeyDown ran (e.g. focus on Window).
                    if (chord.IsTab)
                    {
                        HandleTabChord(keyEvent);
                        return;
                    }

                    bool globalChord = chord.IsCtrlC || chord.IsCtrlQ || chord.IsCtrlK || chord.IsCtrlO
                        || chord.IsCtrlN || chord.IsCtrlR || chord.IsF1 || chord.IsF5 || chord.IsEsc;

                    // When Sessions is logical focus but the TextField still has TG focus,
                    // intercept nav keys so j/k/Enter never type or send from the composer.
                    // Skip when SessionsView already has focus (its KeyDown owns those chords).
                    bool sessionsNav = state.FocusRegion == CommandCenterFocusRegion.Sessions
                        && !window.IsSessionsFocused
                        && (chord.IsEnter || chord.IsUp || chord.IsDown || chord.IsJ || chord.IsK);

                    bool listNav = state.FocusRegion is CommandCenterFocusRegion.Transcript
                            or CommandCenterFocusRegion.Incantations
                        && !window.IsLogFocused
                        && !window.IsIncantationsFocused
                        && (chord.IsUp || chord.IsDown || chord.IsPageUp || chord.IsPageDown
                            || chord.IsHome || chord.IsEnd);

                    if (!globalChord && !sessionsNav && !listNav)
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

                StartThinkingTimer(app, window, state);
                window.FocusInput(app);
                app.Run(window);

                StopThinkingTimer();
                linked.Cancel();
                wardCoordinator.SetUiCallbacks(null, null);
                humanPromptCoordinator.SetUiCallbacks(null, null, null);
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
            await StopHostIfWeStartedItAsync(state).ConfigureAwait(false);

            _actionGate.Dispose();
        }
    }

    /// <summary>
    /// Stops the Arcanum host on exit, but only when this Command Center started it. A host that was
    /// already running belongs to whoever started it; they can stop it with <c>arcanum serve quit</c>
    /// or Ctrl+C in its own terminal. Runs on every exit path, including the terminal-too-small gate,
    /// so a host launched for a session that never opened is not left orphaned. Never throws: failing
    /// to stop the host must not change the CLI exit code.
    /// </summary>
    private async Task StopHostIfWeStartedItAsync(CommandCenterState state)
    {
        if (!ServeOwnershipPolicy.OwnsHost(state.ServeLaunch))
        {
            return;
        }

        try
        {
            Result<bool> quit = await apiClient
                .QuitServerAsync(CancellationToken.None)
                .ConfigureAwait(false);

            if (quit.IsFailure)
            {
                logger.LogWarning(
                    "Could not stop the auto-launched Arcanum host: {ErrorCode}.",
                    quit.Error.Code);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Could not stop the auto-launched Arcanum host; exception type {ExceptionType}.",
                ex.GetType().FullName);
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
                if (humanPromptCoordinator.IsActive
                    || state.Overlay == CommandCenterOverlayKind.HumanPrompt)
                {
                    // Bind submit to turn CTS so CancelTurn cancels in-flight submit (#8).
                    CancellationToken submitToken = state.TurnCts?.Token ?? linked.Token;
                    _ = SubmitHumanPromptAsync(state, window, app, ui, submitToken);
                }
                else
                {
                    submitFromInput();
                }

                break;

            case CommandCenterAction.InsertComposerNewLine:
                app.Invoke(() =>
                {
                    window.InsertComposerNewLine();
                    window.FocusInput();
                });
                break;

            case CommandCenterAction.CancelTurn:
                // Cancel only — Host owns TurnCts disposal. Cancelling TurnCts aborts in-flight submit.
                state.TurnCts?.Cancel();
                _ = humanPromptCoordinator.TryCloseActive(HumanPromptCloseReason.Cancelled);
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
                wardCoordinator.DenyAllPending();
                RequestQuit(state, window, app);
                break;

            case CommandCenterAction.Help:
                if (BlockAuxiliaryWhileHardModal(state, window, app))
                {
                    break;
                }

                ShowHelpOverlay(state, window, app);
                break;

            case CommandCenterAction.CommandPalette:
                if (BlockAuxiliaryWhileHardModal(state, window, app))
                {
                    break;
                }

                ShowPalette(state, window, app);
                break;

            case CommandCenterAction.FocusSessions:
                if (BlockAuxiliaryWhileHardModal(state, window, app))
                {
                    break;
                }

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
                if (state.Overlay == CommandCenterOverlayKind.HumanPrompt
                    || humanPromptCoordinator.IsActive)
                {
                    // Hard modal — Esc does not dismiss HITL.
                    state.FooterHint = "Ctrl+Enter to submit · Ctrl+C to cancel turn";
                    app.Invoke(() => window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter));
                    break;
                }

                if (state.Overlay is CommandCenterOverlayKind.QuitConfirm
                    or CommandCenterOverlayKind.DiscardConfirm
                    or CommandCenterOverlayKind.WardConfirm)
                {
                    CancelPending(state, window, app);
                }
                else if (state.Overlay != CommandCenterOverlayKind.None)
                {
                    CloseOverlayAndFocusInput(state, window, app);
                }
                else
                {
                    state.FocusRegion = CommandCenterFocusRegion.Composer;
                    app.Invoke(() =>
                    {
                        window.FocusInput();
                        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                    });
                }

                break;

            case CommandCenterAction.ToggleTelemetryPane:
                state.ShowTelemetryPane = !state.ShowTelemetryPane;
                app.Invoke(() => window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter));
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
                CloseOverlayAndFocusInput(state, window, app);
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
                if (state.Overlay == CommandCenterOverlayKind.WardConfirm)
                {
                    // Enter defaults to session always-allow (scaffolding rarely wants once-only).
                    _ = wardCoordinator.TryCompletePending(WardApprovalDecision.AllowAlwaysThisTool);
                    CloseOverlayAndFocusInput(state, window, app);
                    break;
                }

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

            case CommandCenterAction.ScrollIncantationsUp:
                window.ScrollIncantationsUp();
                break;

            case CommandCenterAction.ScrollIncantationsDown:
                window.ScrollIncantationsDown();
                break;

            case CommandCenterAction.PageIncantationsUp:
                window.PageIncantationsUp();
                break;

            case CommandCenterAction.PageIncantationsDown:
                window.PageIncantationsDown();
                break;

            case CommandCenterAction.JumpIncantationsHome:
                window.ScrollIncantationsHome();
                break;

            case CommandCenterAction.JumpIncantationsEnd:
                window.ScrollIncantationsEnd();
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
        CloseOverlayAndFocusInput(state, window, app);
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
        if (state.Overlay == CommandCenterOverlayKind.WardConfirm)
        {
            _ = wardCoordinator.TryCompletePending(WardApprovalDecision.Deny);
        }

        _pendingConfirm = null;
        CloseOverlayAndFocusInput(state, window, app);
    }

    private void RequestQuit(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        wardCoordinator.DenyAllPending();

        if (state.Generating)
        {
            _pendingConfirm = new PendingConfirm(PendingConfirmKind.Quit, null);
            state.Overlay = CommandCenterOverlayKind.QuitConfirm;
            state.FocusRegion = CommandCenterFocusRegion.Overlay;
            app.Invoke(() =>
            {
                window.ShowOverlay(
                    CommandCenterOverlayKind.QuitConfirm,
                    ["A turn is still generating.", "Enter = quit anyway", "Esc = cancel"],
                    "Quit?",
                    showFilter: false);
                window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
            });
            return;
        }

        state.RequestExit = true;
        state.ExitCode = 0;
        app.Invoke(() => app.RequestStop());
    }

    private void ShowDiscardConfirm(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        state.Overlay = CommandCenterOverlayKind.DiscardConfirm;
        state.FocusRegion = CommandCenterFocusRegion.Overlay;
        app.Invoke(() =>
        {
            window.ShowOverlay(
                CommandCenterOverlayKind.DiscardConfirm,
                ["Discard unsent composer text?", "Enter = discard", "Esc = cancel"],
                "Discard?",
                showFilter: false);
            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
    }

    private void ShowHelpOverlay(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        state.Overlay = CommandCenterOverlayKind.Help;
        state.FocusRegion = CommandCenterFocusRegion.Overlay;
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
                    "Tab / Shift+Tab Cycle focus (Composer→Sessions→Transcript→Incantations)",
                    "Ctrl+Enter Send (composer)",
                    "Enter Newline (composer)",
                    "Enter Resume (sessions)",
                    "Ctrl+C Cancel turn / clear input / quit hint",
                    "Ctrl+Q Quit",
                    "Esc Close overlay / focus composer",
                    "PgUp/PgDn Transcript scroll (also from composer)",
                    "↑↓/Home/End Scroll focused Transcript or Incantations",
                    "",
                    "Slash: /help /keys /session list|new|resume",
                    "Denied: /serve /daemon… /key…",
                    "",
                    "Incantations: tool calls by CallId (heavy args suppressed)",
                    "Thinking ⠋ while waiting for first token",
                    "",
                    "ask_human: Ctrl+Enter submit · Enter newline · Ctrl+C cancel turn",
                ],
                "Help",
                showFilter: false);
            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
    }

    private void ShowPalette(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        state.Overlay = CommandCenterOverlayKind.CommandPalette;
        state.FocusRegion = CommandCenterFocusRegion.Overlay;
        app.Invoke(() =>
        {
            window.ShowOverlay(
                CommandCenterOverlayKind.CommandPalette,
                PaletteActions,
                "Commands",
                showFilter: false);
            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
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
            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
    }

    private static void CloseOverlayAndFocusInput(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app)
    {
        state.Overlay = CommandCenterOverlayKind.None;
        state.SessionFilter = string.Empty;
        state.FooterHint = null;
        state.FocusRegion = CommandCenterFocusRegion.Composer;
        app.Invoke(() =>
        {
            window.HideOverlayVisual();
            window.FocusInput();
            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
    }

    private static void CycleFocus(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        bool forward)
    {
        CommandCenterFocusRegion desired = CommandCenterFocusCycle.Next(
            state.FocusRegion,
            forward,
            window.SidebarVisible);
        state.FocusRegion = desired;
        state.FooterHint = null;
        app.Invoke(() =>
        {
            ApplyFocusRegion(window, desired);
            // TextView can be sticky — retry once; keep logical FocusRegion even if TG lags
            // (Input.KeyDown already routes by FocusRegion).
            if (window.ResolveFocusedRegion() != desired)
            {
                ApplyFocusRegion(window, desired);
            }

            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
    }

    private static void ApplyFocusRegion(CommandCenterWindow window, CommandCenterFocusRegion region)
    {
        switch (region)
        {
            case CommandCenterFocusRegion.Sessions:
                window.FocusSessions();
                break;
            case CommandCenterFocusRegion.Transcript:
                window.FocusLog();
                break;
            case CommandCenterFocusRegion.Incantations:
                window.FocusIncantations();
                break;
            default:
                window.FocusInput();
                break;
        }
    }

    private void StartThinkingTimer(IApplication app, CommandCenterWindow window, CommandCenterState state)
    {
        StopThinkingTimer();
        _thinkingTimer = new System.Threading.Timer(
            _ =>
            {
                if (!state.ThinkingActive)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _thinkingCallbackQueued, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    app.Invoke(() =>
                    {
                        try
                        {
                            if (!state.ThinkingActive)
                            {
                                return;
                            }

                            state.ThinkingTick++;
                            // Header + ThinkingLabel only — do not rebuild transcript (preserves scroll).
                            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshHeader);
                        }
                        finally
                        {
                            _ = Interlocked.Exchange(ref _thinkingCallbackQueued, 0);
                        }
                    });
                }
                catch
                {
                    _ = Interlocked.Exchange(ref _thinkingCallbackQueued, 0);
                }
            },
            null,
            dueTime: 100,
            period: 100);
    }

    private void StopThinkingTimer()
    {
        System.Threading.Timer? timer = Interlocked.Exchange(ref _thinkingTimer, null);
        timer?.Dispose();
        _ = Interlocked.Exchange(ref _thinkingCallbackQueued, 0);
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
            app.Invoke(() => window.ClearComposer());
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
            // Admission denied — preserve composer text and staged attachments (#10).
            state.Log.Append(SessionLogEntryKind.Status, "Already generating — Ctrl+C to cancel.");
            await ui.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog), linked.Token)
                .ConfigureAwait(false);
            return;
        }

        app.Invoke(() => window.ClearComposer());

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

    private bool TryHandleModalOverlayKey(
        Key e,
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        Action<CommandCenterAction> handle)
    {
        if (state.Overlay is CommandCenterOverlayKind.None or CommandCenterOverlayKind.SessionPicker)
        {
            return false;
        }

        state.FocusRegion = CommandCenterFocusRegion.Overlay;

        if (state.Overlay == CommandCenterOverlayKind.HumanPrompt)
        {
            KeyChord humanChord = ToChord(e);
            if (humanChord.IsEnter && humanChord.IsCtrl)
            {
                e.Handled = true;
                handle(CommandCenterAction.Send);
                return true;
            }

            if (e == Key.Esc)
            {
                e.Handled = true;
                state.FooterHint = "Ctrl+Enter to submit · Ctrl+C to cancel turn";
                window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                return true;
            }

            // Steal focus back to the answer editor; do not type into the composer.
            if (!window.OverlayAnswer.HasFocus)
            {
                window.OverlayAnswer.SetFocus();
            }

            // Let printable / Enter reach OverlayAnswer (newline). Block composer side-effects.
            if (humanChord.IsEnter && !humanChord.IsCtrl)
            {
                return false;
            }

            if (!e.IsCtrl && !e.IsAlt && e != Key.Tab)
            {
                // Character keys: focus answer; one keystroke may be lost if composer had focus.
                e.Handled = true;
                return true;
            }

            return TryMapAndHandle(
                e,
                CommandCenterFocusRegion.Overlay,
                state,
                window,
                handle,
                syncFocusRegion: false);
        }

        if (e == Key.Enter)
        {
            e.Handled = true;
            handle(CommandCenterKeymap.MapOverlayEnter(state.Overlay));
            return true;
        }

        if (e == Key.Esc)
        {
            e.Handled = true;
            if (state.Overlay is CommandCenterOverlayKind.QuitConfirm
                or CommandCenterOverlayKind.DiscardConfirm
                or CommandCenterOverlayKind.WardConfirm)
            {
                CancelPending(state, window, app);
            }
            else
            {
                CloseOverlayAndFocusInput(state, window, app);
            }

            return true;
        }

        if (state.Overlay == CommandCenterOverlayKind.WardConfirm
            && (e == Key.A || e == Key.A.WithShift))
        {
            e.Handled = true;
            _ = wardCoordinator.TryCompletePending(WardApprovalDecision.AllowAlwaysThisTool);
            CloseOverlayAndFocusInput(state, window, app);
            return true;
        }

        if (state.Overlay == CommandCenterOverlayKind.WardConfirm
            && (e == Key.O || e == Key.O.WithShift))
        {
            e.Handled = true;
            _ = wardCoordinator.TryCompletePending(WardApprovalDecision.Allow);
            CloseOverlayAndFocusInput(state, window, app);
            return true;
        }

        if (state.Overlay == CommandCenterOverlayKind.WardConfirm
            && (e == Key.D || e == Key.D.WithShift))
        {
            e.Handled = true;
            _ = wardCoordinator.TryCompletePending(WardApprovalDecision.Deny);
            CloseOverlayAndFocusInput(state, window, app);
            return true;
        }

        if (TryMapAndHandle(
                e,
                CommandCenterFocusRegion.Overlay,
                state,
                window,
                handle,
                syncFocusRegion: false))
        {
            return true;
        }

        // Keep modal keys out of the composer TextView (letters would otherwise type).
        if (!e.IsCtrl && !e.IsAlt && e != Key.Tab)
        {
            e.Handled = true;
            return true;
        }

        return false;
    }

    private bool BlockAuxiliaryWhileHardModal(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app)
    {
        if (!hardModalArbiter.BlocksAuxiliary
            && !humanPromptCoordinator.IsActive
            && state.Overlay is not (CommandCenterOverlayKind.HumanPrompt or CommandCenterOverlayKind.WardConfirm))
        {
            return false;
        }

        state.FooterHint = hardModalArbiter.ActiveKind == CommandCenterHardModalKind.WardConfirm
                || state.Overlay == CommandCenterOverlayKind.WardConfirm
            ? "Resolve the Ward prompt first, or cancel the turn (Ctrl+C)."
            : "Answer the Mage prompt first (Ctrl+Enter), or cancel the turn (Ctrl+C).";
        app.Invoke(() => window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter));
        return true;
    }

    private async Task SubmitHumanPromptAsync(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        ChannelWriter<CommandCenterUiUpdate> ui,
        CancellationToken cancellationToken)
    {
        if (!humanPromptCoordinator.IsActive)
        {
            return;
        }

        string answer = window.GetHumanPromptAnswer();
        HumanPromptSubmitOutcome outcome = await humanPromptCoordinator
            .SubmitAnswerAsync(answer, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is HumanPromptSubmitOutcome.Accepted or HumanPromptSubmitOutcome.NotFound)
        {
            // Overlay closed via onHide callback.
            try
            {
                await ui.WriteAsync(
                        new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshAll),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            return;
        }

        if (outcome == HumanPromptSubmitOutcome.AlreadyInFlight)
        {
            app.Invoke(() =>
            {
                state.FooterHint = "Submit already in progress…";
                window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
            });
            return;
        }

        // TransientFailure / RejectedEmpty — status already pushed via onStatus.
        app.Invoke(() =>
        {
            if (state.Overlay == CommandCenterOverlayKind.HumanPrompt)
            {
                window.OverlayAnswer.SetFocus();
            }

            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
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
            IsCtrlT: key == Key.T.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.T),
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
