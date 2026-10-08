using System.Threading.Channels;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Infrastructure;
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
    IConsoleDispatcher consoleDispatcher,
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    ShellCommandDispatcher dispatcher,
    CommandCenterChatRunner chatRunner,
    CommandCenterApp commandCenterApp,
    SessionWorkspaceService sessionWorkspace,
    CommandCenterHardModalArbiter hardModalArbiter,
    CommandCenterHumanPromptCoordinator humanPromptCoordinator,

    CommandCenterAttachmentDriftMonitor attachmentDriftMonitor,

    ILogger<CommandCenterHost> logger) : ICommandCenterHost
{
    public const string NoCommandCenterEnvVar = "ARCANUM_NO_COMMAND_CENTER";

    /// <summary>
    /// Diagnostic for the viewport size gate. The recovery it names must be a spelling the CLI still
    /// parses, so it never points the operator at a removed command.
    /// </summary>
    internal static string DescribeTerminalTooSmall(int detectedCols, int detectedRows) =>
        "Terminal too small for Command Center. "
        + $"Detected {detectedCols}x{detectedRows}; "
        + $"need at least {CommandCenterApp.MinCols}x{CommandCenterApp.MinRows}. "
        + "Resize the terminal, or run a direct command (e.g. `arcanum run`).";

    /// <summary>Diagnostic for a Command Center that could not start at all.</summary>
    internal const string StartFailureMessage =
        "Command Center failed to start. Try `arcanum run` or another direct command.";

    /// <summary>
    /// Diagnostic for a Command Center that started and then failed while it was running, kept apart from
    /// <see cref="StartFailureMessage"/> so a session that was working is not reported as one that never opened.
    /// </summary>
    internal const string RuntimeFailureMessage =
        "Command Center stopped unexpectedly. Try `arcanum run` or another direct command.";

    /// <summary>
    /// The diagnostic for a failure code <see cref="CommandCenterApp.Run"/> returned, or
    /// <see langword="null"/> when the code is an ordinary exit code.
    /// </summary>
    internal static string? DescribeAppRunFailure(int appRunCode, int detectedCols, int detectedRows) =>
        appRunCode switch
        {
            CommandCenterApp.TooSmall => DescribeTerminalTooSmall(detectedCols, detectedRows),
            CommandCenterApp.StartFailed => StartFailureMessage,
            CommandCenterApp.CrashedAfterStart => RuntimeFailureMessage,
            _ => null,
        };

    /// <summary>
    /// Runs the terminal UI's main loop with the lifecycle around it that has to hold whatever happens.
    /// </summary>
    /// <remarks>
    /// <paramref name="externalToken"/> is the process-level token (SIGTERM or SIGHUP): cancelling it asks
    /// the loop to stop through <paramref name="invokeOnUiThread"/>, because Terminal.Gui only accepts a
    /// stop request on its own thread. The <c>finally</c> cancels <paramref name="runCancellation"/> and
    /// runs <paramref name="cleanup"/> on every exit, including a loop that throws, so the UI pump, the
    /// thinking timer and the human-prompt callbacks never outlive the terminal UI that owned them.
    /// </remarks>
    internal static void RunTerminalLoop(
        CancellationToken externalToken,
        CancellationTokenSource runCancellation,
        Action<Action> invokeOnUiThread,
        Action requestStop,
        Action runLoop,
        Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(runCancellation);
        ArgumentNullException.ThrowIfNull(invokeOnUiThread);
        ArgumentNullException.ThrowIfNull(requestStop);
        ArgumentNullException.ThrowIfNull(runLoop);
        ArgumentNullException.ThrowIfNull(cleanup);

        try
        {
            // Cancelled between start-up and the loop: a stop request sent before the loop exists would
            // be dropped, so do not enter it at all.
            if (externalToken.IsCancellationRequested)
            {
                return;
            }

            using CancellationTokenRegistration stopOnCancel = externalToken.Register(() =>
            {
                try
                {
                    invokeOnUiThread(requestStop);
                }
                catch (Exception)
                {
                    // The loop is already ending or ended, and the canceller (a signal handler) has
                    // nowhere to put an exception.
                }
            });

            runLoop();
        }
        finally
        {
            runCancellation.Cancel();
            cleanup();
        }
    }

    // Written by gated actions on thread-pool threads and read by key handlers on the UI thread.
    private volatile PendingConfirm? _pendingConfirm;

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

    public Task<int> RunAsync(CancellationToken cancellationToken) =>
        RunAsync(startupSessionId: null, cancellationToken);

    public async Task<int> RunAsync(
        Guid? startupSessionId,
        CancellationToken cancellationToken)
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

        // One token for the whole run. The caller's token (SIGTERM or SIGHUP through the launch, or an
        // `open` command) feeds it, and so does Ctrl+C while start-up is still running.
        using CancellationTokenSource runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken hostToken = runCancellation.Token;

        try
        {
            int? startupExitCode = await StartUpAsync(state, startupSessionId, runCancellation).ConfigureAwait(false);
            if (startupExitCode is { } earlyExitCode)
            {
                return earlyExitCode;
            }

            int tgCode = commandCenterApp.Run(
                (app, window) =>
            {
                window.WireResize(app);
                window.ApplyState(state, app);
                window.FocusInput(app);
                state.FocusRegion = CommandCenterFocusRegion.Composer;

                humanPromptCoordinator.SetUiCallbacks(
                    onShow: (request, status) =>
                    {
                        app.Invoke(() =>
                        {
                            // Hard modals are never preempted — the arbiter invokes show only when this
                            // HumanPrompt owns the active slot.
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

                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
                CancellationToken runToken = linked.Token;

                attachmentDriftMonitor.Start(state, uiChannel.Writer, runToken);

                Task pump = Task.Run(
                    () => CommandCenterUiUpdatePump.RunAsync(
                        uiChannel.Reader,
                        work => app.Invoke(work),
                        kind => window.ApplyState(state, kind: kind),
                        logger,
                        runToken),
                    runToken);

                void SubmitFromInput()
                {
                    // Sole entry into HandleSubmitAsync for composer send (see Accepting no-op below).
                    // ClearComposer runs only after TryBeginTurn admits the turn.
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
                        ToChord(e),
                        state.Overlay);
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
                            or CommandCenterFocusRegion.Model
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

                    if (TryOpenSlashMenu(e, routeFocus, state, window))
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

                    // Space, Backspace on a lone slash and Esc leave the slash menu. Decided here, before
                    // the field inserts or deletes anything, and closed synchronously, so the next key
                    // already reaches the composer.
                    if (TryHandleSlashMenuKey(e, state, window))
                    {
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

                    bool modelPicker = state.Overlay == CommandCenterOverlayKind.ModelPicker;

                    if (e == Key.CursorUp || e == Key.CursorDown)
                    {
                        e.Handled = true;
                        int delta = e == Key.CursorUp ? -1 : 1;

                        if (modelPicker)
                        {
                            window.MoveModelSelection(delta, state);
                        }
                        else
                        {
                            window.MoveSessionSelection(delta, state);
                        }

                        return;
                    }

                    // Anything else is the field's own edit. KeyDown runs before the field inserts the key,
                    // so the list follows the edit through TextChanged below instead of from here.
                };

                // TextField raises TextChanged as it inserts each key, so the list it filters is current
                // before the next key is read: an Enter typed in the same burst as the filter runs the
                // row the operator sees. Work that must wait until the field has finished its edit is
                // queued with AddTimeout, because app.Invoke runs at once when already on the UI thread.
                window.OverlayFilter.TextChanged += (_, _) =>
                    ApplyOverlayFilterText(
                        state,
                        window,
                        work => _ = app.AddTimeout(
                            TimeSpan.Zero,
                            () =>
                            {
                                work();

                                return false;
                            }));

                window.ModelSelector.KeyDown += (_, e) =>
                {
                    if (e == Key.Tab || e == Key.Tab.WithShift)
                    {
                        HandleTabChord(e);
                        return;
                    }

                    _ = TryMapAndHandle(e, CommandCenterFocusRegion.Model, state, window, HandleAction);
                };

                void OnOverlayKeyDown(object? _, Key e)
                {
                    if (e == Key.Tab || e == Key.Tab.WithShift)
                    {
                        HandleTabChord(e);
                        return;
                    }

                    // Enter is explicit by overlay kind, ask_human included: there it submits.
                    if (e == Key.Enter)
                    {
                        e.Handled = true;
                        HandleAction(CommandCenterKeymap.MapOverlayEnter(state.Overlay));
                        return;
                    }

                    if (e == Key.Esc
                        && state.Overlay == CommandCenterOverlayKind.HumanPrompt)
                    {
                        e.Handled = true;
                        state.FooterHint = CommandCenterGuidance.HumanPromptFooter;
                        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
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

                    // The composer's key model: every Enter-like chord but bare Enter is a line break, and
                    // bare Enter submits. Both are taken here, before TextView's own Enter binding runs.
                    KeyChord chord = ToChord(e);

                    if (chord.IsNewLine)
                    {
                        e.Handled = true;
                        window.InsertHumanPromptNewLine();
                        return;
                    }

                    if (chord.IsEnter)
                    {
                        e.Handled = true;
                        CancellationToken submitToken = state.TurnTokenOr(linked.Token);
                        _ = SubmitHumanPromptAsync(state, window, app, uiChannel.Writer, submitToken);
                        return;
                    }

                    if (e == Key.Esc)
                    {
                        // Hard modal: Esc does not dismiss; Ctrl+C cancels the turn.
                        e.Handled = true;
                        state.FooterHint = CommandCenterGuidance.HumanPromptFooter;
                        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                        return;
                    }

                    _ = TryMapAndHandle(e, CommandCenterFocusRegion.Overlay, state, window, HandleAction);
                };

                // Send ownership: Input.KeyDown maps bare Enter to CommandCenterAction.Send (SubmitFromInput)
                // and every other Enter-like chord to InsertComposerNewLine, marking the key handled before
                // TextView's own Enter binding runs. With EnterKeyAddsLine true the TextView never raises
                // Accepting; the handler stays a no-op so nothing could double-submit if it ever did.
                window.Input.Accepting += (_, e) =>
                {
                    e.Handled = true;
                };

                app.Keyboard.KeyDown += (_, keyEvent) =>
                {
                    KeyChord chord = ToChord(keyEvent);

                    // Esc in the slash menu hands what was typed back to the composer instead of only
                    // closing the overlay. This handler runs before any view sees the key, so it is the
                    // only place Esc can be given that meaning.
                    if (chord.IsEsc && TryHandleSlashMenuKey(keyEvent, state, window))
                    {
                        return;
                    }

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
                        chord,
                        state.Overlay);

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

                RunTerminalLoop(
                    hostToken,
                    linked,
                    invokeOnUiThread: action => app.Invoke(action),
                    requestStop: () => app.RequestStop(),
                    runLoop: () => app.Run(window),
                    cleanup: () =>
                    {
                        StopThinkingTimer();
                        humanPromptCoordinator.SetUiCallbacks(null, null, null);
                        uiChannel.Writer.TryComplete();
                        try
                        {
                            pump.GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException)
                        {
                            // The pump ends by cancellation once the run token is cancelled.
                        }
                        catch (Exception ex)
                        {
                            // Nothing else observes the pump, so its fault is reported here.
                            logger.LogError(ex, "The Command Center UI update pump failed.");
                        }
                    });

                // The loop ended because the process was asked to stop, not because the operator quit.
                if (hostToken.IsCancellationRequested && !state.RequestExit)
                {
                    state.ExitCode = (int)CliExitCode.Cancelled;
                }

                return state.ExitCode;
            },
                state.MonochromeTheme);

            if (DescribeAppRunFailure(
                    tgCode,
                    commandCenterApp.LastDetectedCols,
                    commandCenterApp.LastDetectedRows) is { } failureMessage)
            {
                Console.Error.WriteLine(failureMessage);
                return 1;
            }

            return state.RequestExit ? state.ExitCode : tgCode;
        }
        catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
        {
            // Ctrl+C during startup (auto-serve readiness, MCP refresh, session restore) is a
            // cancellation, not a host failure: the CLI contract fixes it at 130 for every verb.
            logger.LogDebug("Command Center start-up was cancelled.");
            return (int)CliExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command Center host failed.");
            Console.Error.WriteLine($"Command Center error: {ex.Message}");
            return 1;
        }
        finally
        {
            await attachmentDriftMonitor.DisposeAsync().ConfigureAwait(false);

            await StopHostIfWeStartedItAsync(state).ConfigureAwait(false);

            _actionGate.Dispose();
        }
    }

    /// <summary>
    /// Everything that happens before the terminal UI opens: the auto-serve launch, the MCP refresh and
    /// the session restore. Returns an exit code when start-up ends the run (the server cannot be used),
    /// and <see langword="null"/> when the UI should open.
    /// </summary>
    /// <remarks>
    /// Until the terminal UI owns the console, Ctrl+C is a signal rather than a key, so it cancels the
    /// run and start-up unwinds to exit 130 with the usual cleanup. The handler is removed before the UI
    /// opens: from then on Ctrl+C is the UI's own cancel-turn key.
    /// </remarks>
    private async Task<int?> StartUpAsync(
        CommandCenterState state,
        Guid? startupSessionId,
        CancellationTokenSource runCancellation)
    {
        CancellationToken cancellationToken = runCancellation.Token;

        CommandCenterStartUpInterrupt interrupt = new(() => runCancellation.Cancel());

        // Cancel stays false for a later interrupt, so the default action ends a process whose start-up
        // never looks at its token.
        void OnInterrupt(object? sender, ConsoleCancelEventArgs e) => e.Cancel = interrupt.Absorb();

        Console.CancelKeyPress += OnInterrupt;

        try
        {
            ServeLaunchResult launch = await serveLauncher
                .EnsureRunningAsync(cancellationToken)
                .ConfigureAwait(false);
            state.ServeLaunch = launch;
            state.HealthSummary = launch.Guidance;

            if (!ServeOwnershipPolicy.CanProceed(launch))
            {
                consoleDispatcher.WriteDiagnostic(
                    launch.Guidance
                        ?? "Arcanum could not start or authenticate its local server.");

                return (int)ServeOwnershipPolicy.FailureExitCode(launch);
            }

            await dispatcher.RefreshMcpAsync(state, cancellationToken).ConfigureAwait(false);
            if (startupSessionId is { } sessionId)
            {
                _ = await sessionWorkspace
                    .ResumeSessionAsync(state, sessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await sessionWorkspace
                    .RestoreStartupSessionAsync(state, cancellationToken)
                    .ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            Console.CancelKeyPress -= OnInterrupt;
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

    /// <summary>
    /// Runs a gated action that decides from window state: <paramref name="capture"/> runs first, before
    /// the gate is awaited, so it runs on the caller's thread — the UI thread, when called from a key
    /// handler — however long the gate stays contended. <paramref name="action"/> then receives the
    /// captured values and never touches a view to learn them.
    /// </summary>
    internal async Task RunGatedAsync<TInput>(
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> ui,
        CancellationToken cancellationToken,
        Func<TInput> capture,
        Func<TInput, Task> action)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(action);

        TInput input = capture();

        await RunGatedAsync(state, ui, cancellationToken, () => action(input)).ConfigureAwait(false);
    }

    internal async Task RunGatedAsync(
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
                    // Bind submit to turn CTS so CancelTurn cancels in-flight submit.
                    CancellationToken submitToken = state.TurnTokenOr(linked.Token);
                    _ = SubmitHumanPromptAsync(state, window, app, ui, submitToken);
                }
                else
                {
                    submitFromInput();
                }

                break;

            case CommandCenterAction.InsertComposerNewLine:
                // Only key handlers on the UI thread produce this action, so the break goes in now:
                // deferring it through app.Invoke would let the next key of a fast burst land before it.
                window.InsertComposerNewLine();
                window.FocusInput();
                break;

            case CommandCenterAction.CancelTurn:
                // Cancel only — Host owns TurnCts disposal. Cancelling TurnCts aborts in-flight submit.
                _ = state.TryCancelTurn();
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
                        capture: () => SessionActionWindowState.Capture(window, state),
                        action: input => RequestNewSessionCoreAsync(state, window, app, ui, input, linked.Token))
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
                    state.FooterHint = CommandCenterGuidance.HumanPromptFooter;
                    app.Invoke(() => window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter));
                    break;
                }

                if (state.Overlay is CommandCenterOverlayKind.QuitConfirm
                    or CommandCenterOverlayKind.DiscardConfirm)
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
                app.Invoke(() => window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar));
                break;

            case CommandCenterAction.SessionSelectUp:
                window.MoveSessionSelection(-1, state);
                break;

            case CommandCenterAction.SessionSelectDown:
                window.MoveSessionSelection(1, state);
                break;

            case CommandCenterAction.PaletteSelectUp:
                window.MovePaletteSelection(-1);
                break;

            case CommandCenterAction.PaletteSelectDown:
                window.MovePaletteSelection(1);
                break;

            case CommandCenterAction.OpenModelPicker:
                if (BlockAuxiliaryWhileHardModal(state, window, app))
                {
                    break;
                }

                await OpenModelPickerAsync(state, window, app, linked.Token).ConfigureAwait(false);
                break;

            case CommandCenterAction.ModelSelectUp:
                window.MoveModelSelection(-1, state);
                break;

            case CommandCenterAction.ModelSelectDown:
                window.MoveModelSelection(1, state);
                break;

            case CommandCenterAction.SelectModel:
                ApplySelectedModel(state, window, app);
                break;

            case CommandCenterAction.LoadOlderSessionPage:
                await LoadSessionPageCoreAsync(
                        state,
                        window,
                        app,
                        ui,
                        older: true,
                        cancellationToken: linked.Token)
                    .ConfigureAwait(false);
                break;

            case CommandCenterAction.LoadNewerSessionPage:
                await LoadSessionPageCoreAsync(
                        state,
                        window,
                        app,
                        ui,
                        older: false,
                        cancellationToken: linked.Token)
                    .ConfigureAwait(false);
                break;

            case CommandCenterAction.ResumeSelectedSession:
                await RunGatedAsync(
                        state,
                        ui,
                        linked.Token,
                        capture: () => SessionActionWindowState.Capture(window, state),
                        action: input => ResumeSelectedCoreAsync(state, window, app, ui, input, linked.Token))
                    .ConfigureAwait(false);
                break;

            case CommandCenterAction.ExecutePaletteItem:
            {
                // Reached from an Enter key handler with nothing awaited yet, so the window is read and
                // the palette closed on the UI thread, before any gate is waited on.
                if (state.PaletteMode == CommandPaletteMode.Slash)
                {
                    // The same path as typing the line and sending it, so drift refresh, alternative
                    // prompts and parser errors behave exactly as they do from the composer.
                    if (CommitSlashMenu(state, window))
                    {
                        submitFromInput();
                    }

                    break;
                }

                CommandPaletteEntry? entry = SelectedPaletteEntry(state, window);

                ClosePaletteNow(state, window, composerText: null);

                if (entry is not null)
                {
                    await RunPaletteActionAsync(
                            entry,
                            state,
                            ui,
                            app,
                            window,
                            linked,
                            submitFromInput)
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

            case CommandCenterAction.LoadOlderTranscriptPage:
                await LoadTranscriptPageCoreAsync(
                        state,
                        window,
                        app,
                        ui,
                        older: true,
                        cancellationToken: linked.Token)
                    .ConfigureAwait(false);
                break;

            case CommandCenterAction.LoadNewerTranscriptPage:
                await LoadTranscriptPageCoreAsync(
                        state,
                        window,
                        app,
                        ui,
                        older: false,
                        cancellationToken: linked.Token)
                    .ConfigureAwait(false);
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

    private async Task LoadTranscriptPageCoreAsync(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        ChannelWriter<CommandCenterUiUpdate> ui,
        bool older,
        CancellationToken cancellationToken)
    {
        if (state.Generating)
        {
            state.FooterHint = "Finish or cancel the active turn before changing transcript pages.";

            await ui.WriteAsync(
                    new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshFooter),
                    cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        bool loaded = older
            ? await sessionWorkspace
                .LoadOlderTranscriptPageAsync(state, cancellationToken)
                .ConfigureAwait(false)
            : await sessionWorkspace
                .LoadNewerTranscriptPageAsync(state, cancellationToken)
                .ConfigureAwait(false);

        if (!loaded)
        {
            state.FooterHint = state.LastError
                ?? (older
                    ? "No older transcript page is available."
                    : "No newer transcript page is available.");

            await ui.WriteAsync(
                    new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshFooter),
                    cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        state.FooterHint = older
            ? "Loaded older transcript page · Ctrl+PgDn returns toward the latest entries"
            : "Loaded newer transcript page · Ctrl+PgUp returns toward older entries";

        app.Invoke(() =>
        {
            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshLog);

            if (older)
            {
                window.ScrollLogEnd();
            }
            else
            {
                window.ScrollLogHome();
            }

            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
    }

    private async Task LoadSessionPageCoreAsync(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        ChannelWriter<CommandCenterUiUpdate> ui,
        bool older,
        CancellationToken cancellationToken)
    {
        if (state.Generating)
        {
            state.FooterHint = "Finish or cancel the active turn before changing session pages.";

            await ui.WriteAsync(
                    new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshFooter),
                    cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        bool loaded = older
            ? await sessionWorkspace
                .LoadOlderSessionsPageAsync(state, cancellationToken)
                .ConfigureAwait(false)
            : await sessionWorkspace
                .LoadNewerSessionsPageAsync(state, cancellationToken)
                .ConfigureAwait(false);

        if (!loaded)
        {
            state.FooterHint = state.LastError
                ?? (older
                    ? "No older session page is available."
                    : "No newer session page is available.");

            await ui.WriteAsync(
                    new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshFooter),
                    cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        state.SessionFilter = string.Empty;

        state.FooterHint = older
            ? "Loaded older sessions · Ctrl+PgUp returns toward recent sessions"
            : "Loaded newer sessions · Ctrl+PgDn continues toward older sessions";

        app.Invoke(() =>
        {
            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

            window.EnsureSessionSelection(state);

            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
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
        SessionActionWindowState input,
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
            if (SessionIdLineParser.TryExtractNear(input.LogLines, input.SelectedLogIndex, out Guid logId))
            {
                await ResumeWithConfirmCoreAsync(state, window, app, ui, logId, input.ComposerHasText, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (input.SelectedSessionId is not { } id)
        {
            return;
        }

        await ResumeWithConfirmCoreAsync(state, window, app, ui, id, input.ComposerHasText, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ResumeWithConfirmCoreAsync(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        ChannelWriter<CommandCenterUiUpdate> ui,
        Guid sessionId,
        bool composerHasText,
        CancellationToken cancellationToken)
    {
        if (CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? deny))
        {
            await ui.WriteAsync(deny!, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (composerHasText)
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
        SessionActionWindowState input,
        CancellationToken cancellationToken)
    {
        if (CommandCenterSessionMutationGuard.TryDenySessionMutationWhileGenerating(state, out CommandCenterUiUpdate? deny))
        {
            await ui.WriteAsync(deny!, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (input.ComposerHasText)
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
        _pendingConfirm = null;
        CloseOverlayAndFocusInput(state, window, app);
    }

    private void RequestQuit(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
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

    /// <summary>
    /// Body of the F1 overlay. The <c>Slash:</c> line is operator instruction, so every spelling it
    /// lists must be one <see cref="ShellCommandParser"/> still accepts. Enter sends and Ctrl+J inserts
    /// a line break, in the composer and the ask_human answer alike: Ctrl+J is a line feed in every
    /// terminal, while Ctrl+Enter arrives as the same CR as Enter in most of them.
    /// </summary>
    internal static readonly string[] HelpOverlayLines =
    [
        HelpRow("Enter", "Send the message or /command (composer) · submit (ask_human)"),
        HelpRow("Ctrl+J", "New line · also Alt+Enter, Shift+Enter, Ctrl+Enter where the terminal sends them"),
        HelpRow("/", "Slash-command menu (in an empty composer)"),
        HelpRow("Ctrl+K", "Command palette"),
        HelpRow("Ctrl+N", "New session"),
        HelpRow("Ctrl+O", "Sessions"),
        HelpRow("Shift+Tab", "Model control from the composer · Enter opens the model list"),
        HelpRow("Tab / Shift+Tab", "Cycle focus (Composer→Sessions→Transcript→Incantations→Model)"),
        HelpRow("Enter", "Resume the selected session (Sessions)"),
        HelpRow("Ctrl+R / F5", "Refresh"),
        HelpRow("Ctrl+C", "Cancel turn / clear input / quit hint"),
        HelpRow("Ctrl+Q", "Quit"),
        HelpRow("Esc", "Close overlay / focus composer"),
        HelpRow("PgUp/PgDn", "Transcript scroll (also from composer)"),
        HelpRow("Ctrl+PgUp/PgDn", "Load adjacent Transcript or Sessions page"),
        HelpRow("↑↓/Home/End", "Scroll the focused Transcript, Incantations or this help"),
        string.Empty,
        "Slash: /help /keys /model /session list /clear /resume <id>",
        "Denied: /serve /daemon… /key…",
        string.Empty,
        "Incantations: tool calls by CallId (heavy args suppressed)",
        "Thinking ⠋ while waiting for first token",
        string.Empty,
        $"ask_human: {CommandCenterGuidance.HumanPromptFooter}",
    ];

    /// <summary>One <see cref="HelpOverlayLines"/> row: the key padded to a 19-cell column, then what it does.</summary>
    private static string HelpRow(string key, string text) => $"{key,-19}{text}";

    /// <summary>The <c>Slash:</c> summary line of <see cref="HelpOverlayLines"/>.</summary>
    internal static string HelpOverlaySlashSummary =>
        HelpOverlayLines.First(static line => line.StartsWith("Slash:", StringComparison.Ordinal));

    private void ShowHelpOverlay(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        state.Overlay = CommandCenterOverlayKind.Help;
        state.FocusRegion = CommandCenterFocusRegion.Overlay;
        app.Invoke(() =>
        {
            // The help is a scrollable list, so its title says how to move through it and out of it.
            window.ShowOverlay(
                CommandCenterOverlayKind.Help,
                HelpOverlayLines,
                "Help · ↑↓ scroll · Esc close",
                showFilter: false);
            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
    }

    /// <summary>
    /// Opens the model drop-down, loading the offered models first. The list comes from the API, so
    /// it reflects the hide list and every provider kind without Command Center knowing about either.
    /// </summary>
    private async Task OpenModelPickerAsync(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app,
        CancellationToken cancellationToken)
    {
        Result<ModelInfoDto[]> models = await apiClient
            .GetModelsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (models.IsFailure)
        {
            // The host being down is a first-class state, not a spinner: say so and leave `/model`
            // working, since it fails the same way and tells the operator the same thing.
            state.Log.Append(SessionLogEntryKind.Error, models.Error.Message);

            app.Invoke(() => window.ApplyState(state, forceFollowTail: true));

            return;
        }

        state.ModelChoices = CommandCenterModelPicker.Build(models.Value ?? []);
        state.ModelFilter = string.Empty;
        state.Overlay = CommandCenterOverlayKind.ModelPicker;
        state.FocusRegion = CommandCenterFocusRegion.Overlay;

        app.Invoke(() =>
        {
            window.ShowModelPickerOverlay(state);
            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
        });
    }

    /// <summary>
    /// Commits the highlighted model into the same session slot <c>/model &lt;name&gt;</c> sets, so
    /// the drop-down and the slash command can never disagree about the current model.
    /// </summary>
    private static void ApplySelectedModel(
        CommandCenterState state,
        CommandCenterWindow window,
        IApplication app)
    {
        ModelPickerItem? selected = CommandCenterModelPicker.ResolveItem(
            state.FilteredModels,
            state.SelectedModelIndex);

        if (selected is not null)
        {
            state.Model = selected.Model;

            // The same words `/model <name>` uses for a listed model: every row here is one.
            state.Log.Append(
                SessionLogEntryKind.Status,
                CommandCenterModelChoice.Confirmation(selected.Model, selected.ProviderName));
        }

        CloseOverlayAndFocusInput(state, window, app);

        app.Invoke(() => window.ApplyState(state, forceFollowTail: true));
    }

    private void ShowPalette(CommandCenterState state, CommandCenterWindow window, IApplication app)
    {
        OpenPaletteNow(state, window, CommandPaletteMode.Actions, string.Empty);
    }

    private async Task RunPaletteActionAsync(
        CommandPaletteEntry entry,
        CommandCenterState state,
        ChannelWriter<CommandCenterUiUpdate> ui,
        IApplication app,
        CommandCenterWindow window,
        CancellationTokenSource linked,
        Action submitFromInput)
    {
        switch (entry.Target)
        {
            case CommandPaletteTarget.NewSession:
                await RunGatedAsync(
                        state,
                        ui,
                        linked.Token,
                        capture: () => SessionActionWindowState.Capture(window, state),
                        action: input => RequestNewSessionCoreAsync(state, window, app, ui, input, linked.Token))
                    .ConfigureAwait(false);
                break;

            case CommandPaletteTarget.ChooseModel:
                // The header control's own drop-down, so it is gated exactly as the header control is.
                await DispatchActionAsync(
                        CommandCenterAction.OpenModelPicker,
                        state,
                        ui,
                        app,
                        window,
                        linked,
                        submitFromInput)
                    .ConfigureAwait(false);
                break;

            case CommandPaletteTarget.OpenSessions:
                await DispatchActionAsync(
                        CommandCenterAction.FocusSessions,
                        state,
                        ui,
                        app,
                        window,
                        linked,
                        submitFromInput)
                    .ConfigureAwait(false);
                break;

            case CommandPaletteTarget.BrowseSlashCommands:
                // Nothing has been awaited on the way here, so this is still the Enter key's UI turn.
                OpenSlashMenuFromPalette(state, window);
                break;

            case CommandPaletteTarget.Refresh:
                await RunGatedAsync(
                        state,
                        ui,
                        linked.Token,
                        () => RefreshSessionsCoreAsync(state, ui, linked.Token))
                    .ConfigureAwait(false);
                break;

            case CommandPaletteTarget.Help:
                ShowHelpOverlay(state, window, app);
                break;

            case CommandPaletteTarget.Quit:
                RequestQuit(state, window, app);
                break;

            case CommandPaletteTarget.RunSlashText when entry.SlashText is { } slashText:
                await DispatchSlashAsync(slashText, state, ui, app, window, linked).ConfigureAwait(false);
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
        app.Invoke(() => state.SelectedTranscriptEntryId = window.GetSelectedTranscriptEntryId(state));
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

    /// <summary>
    /// Opens the palette, or the slash menu, in the key handler that asked for it. Every caller is a
    /// key handler on the UI thread, and the open happens in place rather than through anything that
    /// can wait for a later main-loop turn: the keys typed straight after <c>Ctrl+K</c> or <c>/</c>
    /// must land in the palette's filter, and an open that waits lets them reach the composer first.
    /// </summary>
    internal static void OpenPaletteNow(
        CommandCenterState state,
        CommandCenterWindow window,
        CommandPaletteMode mode,
        string filter)
    {
        state.Overlay = CommandCenterOverlayKind.CommandPalette;

        state.FocusRegion = CommandCenterFocusRegion.Overlay;

        state.PaletteMode = mode;

        state.PaletteFilter = filter;

        state.FooterHint = null;

        window.ShowPaletteOverlay(state);

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
    }

    /// <summary>
    /// Closes the palette from the key handler that decided to, handing <paramref name="composerText"/>
    /// to the composer when there is a line to hand back. Synchronous for the same reason as
    /// <see cref="OpenPaletteNow"/>: the next key must reach the composer, not the closing palette.
    /// </summary>
    internal static void ClosePaletteNow(
        CommandCenterState state,
        CommandCenterWindow window,
        string? composerText)
    {
        // The overlay is marked closed before the filter is cleared, so the TextChanged the clear raises
        // is ignored rather than read as one more filter edit.
        state.Overlay = CommandCenterOverlayKind.None;

        state.PaletteFilter = string.Empty;

        state.FocusRegion = CommandCenterFocusRegion.Composer;

        state.FooterHint = null;

        window.HideOverlayVisual();

        if (composerText is not null)
        {
            window.SetComposerText(composerText);
        }

        window.FocusInput();

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
    }

    /// <summary>
    /// A <c>/</c> typed into a composer that is truly empty — not merely blank — opens the slash menu
    /// with the slash already in its filter. Anywhere else the slash is ordinary text.
    /// </summary>
    internal static bool TryOpenSlashMenu(
        Key key,
        CommandCenterFocusRegion routeFocus,
        CommandCenterState state,
        CommandCenterWindow window)
    {
        if (routeFocus != CommandCenterFocusRegion.Composer
            || state.Overlay != CommandCenterOverlayKind.None
            || key.IsCtrl
            || key.IsAlt
            || TryGetChar(key) != '/'
            || window.GetComposerText().Length != 0)
        {
            return false;
        }

        key.Handled = true;

        OpenPaletteNow(state, window, CommandPaletteMode.Slash, "/");

        return true;
    }

    /// <summary>Why the palette's Slash Commands entry did not open the slash menu over a draft.</summary>
    internal const string SlashMenuNeedsEmptyComposer =
        "Slash Commands opens over an empty composer: send or clear the draft, then type /.";

    /// <summary>
    /// The palette's Slash Commands entry. Every way out of the slash menu writes its line into the
    /// composer, so the menu opens only over a composer with nothing in it; over a draft the footer
    /// says why rather than letting the first key that leaves the menu overwrite the draft.
    /// </summary>
    internal static void OpenSlashMenuFromPalette(CommandCenterState state, CommandCenterWindow window)
    {
        if (!window.ComposerHasText)
        {
            OpenPaletteNow(state, window, CommandPaletteMode.Slash, "/");

            return;
        }

        state.FooterHint = SlashMenuNeedsEmptyComposer;

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
    }

    /// <summary>
    /// The slash menu's own keys: a space hands the typed line to the composer, Backspace on a lone
    /// slash leaves the menu with nothing typed, and <c>Esc</c> leaves it with what was typed. Any
    /// other key, or any key outside the slash menu, is left to the view that has it.
    /// </summary>
    internal static bool TryHandleSlashMenuKey(Key key, CommandCenterState state, CommandCenterWindow window)
    {
        if (state.Overlay != CommandCenterOverlayKind.CommandPalette
            || state.PaletteMode != CommandPaletteMode.Slash)
        {
            return false;
        }

        SlashMenuKey? menuKey = key == Key.Space
            ? SlashMenuKey.Space
            : key == Key.Backspace
                ? SlashMenuKey.Backspace
                : key == Key.Esc
                    ? SlashMenuKey.Escape
                    : null;

        if (menuKey is not { } slashKey
            || CommandPaletteCatalog.DecideSlashKey(window.OverlayFilter.Text ?? string.Empty, slashKey, selected: null)
                is not { } step)
        {
            return false;
        }

        key.Handled = true;

        ClosePaletteNow(state, window, step.Text);

        return true;
    }

    /// <summary>
    /// Enter in the slash menu. A command that takes no argument, or a line nothing matches, is left
    /// in the composer and <see langword="true"/> is returned: the caller then sends it exactly as if
    /// it had been typed there. A command that takes an argument is completed in the composer instead.
    /// </summary>
    internal static bool CommitSlashMenu(CommandCenterState state, CommandCenterWindow window)
    {
        CommandPaletteEntry? selected = SelectedPaletteEntry(state, window);

        if (CommandPaletteCatalog.DecideSlashKey(window.OverlayFilter.Text ?? string.Empty, SlashMenuKey.Enter, selected)
            is not { } step)
        {
            return false;
        }

        ClosePaletteNow(state, window, step.Text);

        return step.Kind == SlashMenuStepKind.Run;
    }

    /// <summary>The palette entry under the highlight, or <see langword="null"/> when nothing matched.</summary>
    internal static CommandPaletteEntry? SelectedPaletteEntry(CommandCenterState state, CommandCenterWindow window)
    {
        IReadOnlyList<CommandPaletteEntry> entries = state.FilteredPaletteEntries;

        int index = window.GetOverlaySelectedIndex();

        return index >= 0 && index < entries.Count ? entries[index] : null;
    }

    /// <summary>
    /// The overlay filter's text changed: narrow the list it filters at once. In the slash menu, an
    /// edit that left more than a bare <c>/name</c> — a paste carrying an argument, or a slash deleted
    /// from the front — hands the line to the composer through <paramref name="invokeLater"/>, once the
    /// field has finished the edit it is raising this from.
    /// </summary>
    internal static void ApplyOverlayFilterText(
        CommandCenterState state,
        CommandCenterWindow window,
        Action<Action> invokeLater)
    {
        string typed = window.OverlayFilter.Text ?? string.Empty;

        switch (state.Overlay)
        {
            case CommandCenterOverlayKind.ModelPicker
                when !string.Equals(typed, state.ModelFilter, StringComparison.Ordinal):
                state.ModelFilter = typed;

                window.RefreshModelList(state);

                break;

            case CommandCenterOverlayKind.CommandPalette
                when !string.Equals(typed, state.PaletteFilter, StringComparison.Ordinal):
                state.PaletteFilter = typed;

                window.RefreshPaletteList(state);

                if (state.PaletteMode == CommandPaletteMode.Slash
                    && CommandPaletteCatalog.DecideSlashFilterEdit(typed) is not null)
                {
                    invokeLater(() => HandSlashFilterToComposer(state, window));
                }

                break;

            case CommandCenterOverlayKind.SessionPicker
                when !string.Equals(typed, state.SessionFilter, StringComparison.Ordinal):
                state.SessionFilter = typed;

                window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

                break;
        }
    }

    /// <summary>
    /// The deferred half of <see cref="ApplyOverlayFilterText"/>: re-reads the filter, because more keys
    /// may have reached it since, and hands it to the composer if the slash menu still needs to close.
    /// </summary>
    private static void HandSlashFilterToComposer(CommandCenterState state, CommandCenterWindow window)
    {
        if (state.Overlay == CommandCenterOverlayKind.CommandPalette
            && state.PaletteMode == CommandPaletteMode.Slash
            && CommandPaletteCatalog.DecideSlashFilterEdit(window.OverlayFilter.Text ?? string.Empty) is { } step)
        {
            ClosePaletteNow(state, window, step.Text);
        }
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
            window.SidebarVisible,
            window.ModelSelectorVisible);
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
            case CommandCenterFocusRegion.Model:
                window.FocusModelSelector();
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
            app.Invoke(() => state.SelectedTranscriptEntryId = window.GetSelectedTranscriptEntryId(state));
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

                        attachmentDriftMonitor.RequestRefresh();

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
            if (state.PendingAlternativePrompt is { } alternativePrompt)
            {
                state.PendingAlternativePrompt = null;
                await HandleSubmitAsync(alternativePrompt, state, ui, app, window, linked)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (!state.TryBeginTurn())
        {
            // Admission denied — preserve composer text and staged attachments.
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

            attachmentDriftMonitor.RequestRefresh();

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

            // A line break belongs in the answer, never the composer that still holds the keyboard.
            if (humanChord.IsNewLine)
            {
                e.Handled = true;

                if (!window.OverlayAnswer.HasFocus)
                {
                    window.OverlayAnswer.SetFocus();
                }

                window.InsertHumanPromptNewLine();

                return true;
            }

            if (humanChord.IsEnter)
            {
                e.Handled = true;

                handle(CommandCenterAction.Send);

                return true;
            }

            if (e == Key.Esc)
            {
                e.Handled = true;
                state.FooterHint = CommandCenterGuidance.HumanPromptFooter;
                window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshFooter);
                return true;
            }

            // Steal focus back to the answer editor; do not type into the composer.
            if (!window.OverlayAnswer.HasFocus)
            {
                window.OverlayAnswer.SetFocus();
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
                or CommandCenterOverlayKind.DiscardConfirm)
            {
                CancelPending(state, window, app);
            }
            else
            {
                CloseOverlayAndFocusInput(state, window, app);
            }

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
            && state.Overlay != CommandCenterOverlayKind.HumanPrompt)
        {
            return false;
        }

        state.FooterHint = "Answer the Mage prompt first (Enter submits), or cancel the turn (Ctrl+C).";
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
            chord,
            state.Overlay);

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

    private static char? TryGetChar(Key key) => CommandCenterKeyChords.TryGetPrintableChar(key);

    private static KeyChord ToChord(Key key) => CommandCenterKeyChords.FromKey(key);

    private enum PendingConfirmKind
    {
        Quit,
        NewSession,
        ResumeSession,
    }

    private sealed record PendingConfirm(PendingConfirmKind Kind, Guid? SessionId);
}
