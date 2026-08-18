using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// Console HITL lifecycle for <c>ask_human</c>: continues NDJSON pumping while input is pending,
/// races operator input vs ToolError/Result/Error/timeout/cancel, and ensures exactly one console
/// input owner. Abandoned reads are drained so they cannot steal the next REPL line.
///
/// <para>The draining is bounded. The shipped reader wraps <c>Console.ReadKey</c>, which no
/// cancellation token can interrupt, so waiting for an abandoned read to end is waiting for the
/// operator to type — after the answer is already on stdout and the command is trying to exit. Past
/// <see cref="AbandonedReadGrace" /> the read is disowned instead: its result is discarded, and the
/// single-owner guard keeps a later prompt from racing it for the operator's keystrokes.</para>
/// </summary>
internal sealed class ConsoleAskHumanCoordinator
{
    /// <summary>
    /// How long a dismissed prompt waits for its abandoned read before disowning it. Long enough for
    /// a reader that can observe its token to unwind, short enough that a reader that cannot never
    /// becomes the reason the command will not exit.
    /// </summary>
    private static readonly TimeSpan AbandonedReadGrace = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly ArcanumApiClient _apiClient;
    private readonly IThemePalette _palette;
    private readonly IAnsiConsole _diagnosticConsole;
    private readonly Func<string, bool, CancellationToken, Task<string?>> _readLineAsync;
    private readonly Action? _onOperatorInterrupt;

    private PendingHitl? _pending;
    private Task? _raceTask;
    private Task<string?>? _disownedRead;
    private AskHumanResult? _settledResult;
    private int _generation;

    private sealed class PendingHitl(
        string promptId,
        string? callId,
        string question,
        int generation,
        TaskCompletionSource dismissTcs)
    {
        public string PromptId { get; } = promptId;
        public string? CallId { get; } = callId;
        public string Question { get; } = question;
        public int Generation { get; } = generation;
        public TaskCompletionSource DismissTcs { get; } = dismissTcs;
        public bool SubmitStarted { get; set; }
    }

    /// <summary>
    /// Operator diagnostics go to <paramref name="diagnosticConsole" /> (stderr by default), never to
    /// the process-global console: the non-interactive branch below is exactly the redirected and
    /// <c>--json</c> case, where stdout carries the assistant answer. Only the interactive prompt
    /// itself is rendered on stdout, by the caller-supplied read-line delegate.
    ///
    /// <para><paramref name="onOperatorInterrupt" /> is how a Ctrl+C typed into the prompt reaches the
    /// caller. The read captures that keystroke rather than letting it raise SIGINT, so
    /// <c>Console.CancelKeyPress</c> never runs, and this coordinator's own token is a child of the
    /// caller's — cancelling it cannot travel upward. A caller that supplies nothing keeps the old
    /// behaviour: the prompt settles and the turn runs on.</para>
    /// </summary>
    public ConsoleAskHumanCoordinator(
        ArcanumApiClient apiClient,
        IThemePalette palette,
        Func<string, bool, CancellationToken, Task<string?>>? readLineAsync = null,
        IAnsiConsole? diagnosticConsole = null,
        Action? onOperatorInterrupt = null)
    {
        _apiClient = apiClient;
        _palette = palette;
        _diagnosticConsole = diagnosticConsole ?? CreateStandardErrorConsole();
        _readLineAsync = readLineAsync ?? DefaultReadLineAsync;
        _onOperatorInterrupt = onOperatorInterrupt;
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>
    /// Handles an <c>ask_human</c> ToolCall without blocking the stream pump for interactive input.
    /// Unattended/non-interactive paths submit immediately. Interactive paths start a background race.
    /// </summary>
    public async Task<AskHumanResult> TryBeginAsync(
        IntelligenceEvent evt,
        bool unattended,
        bool isInteractive,
        CancellationToken cancellationToken)
    {
        if (evt.Type != IntelligenceEventType.ToolCall)
        {
            return AskHumanResult.NotHandled;
        }

        string toolName = evt.ToolCall?.Name ?? evt.Message;
        if (!string.Equals(toolName, "ask_human", StringComparison.Ordinal))
        {
            return AskHumanResult.NotHandled;
        }

        if (!AskHumanToolCallStreamHandler.TryParseAskHumanArgs(evt, out AskHumanParams? args, out string? parseError)
            || args is null)
        {
            if (parseError is not null)
            {
                _diagnosticConsole.MarkupLine(_palette.ErrorMarkup(Markup.Escape(parseError)));
                return AskHumanResult.ParseFailed;
            }

            return AskHumanResult.NotHandled;
        }

        if (unattended || !isInteractive)
        {
            string autoReply = unattended
                ? "System: The user is in unattended mode. Proceed using your best judgment."
                : "System: No interactive terminal is available. Proceed using your best judgment.";

            Result<bool> submitResult = await _apiClient
                .SubmitHumanResponseAsync(args.PromptId, autoReply, cancellationToken)
                .ConfigureAwait(false);

            if (submitResult.IsFailure)
            {
                _diagnosticConsole.MarkupLine(
                    _palette.ErrorMarkup(Markup.Escape(
                        $"Failed to submit response to Daemon ({submitResult.Error.Code}): {submitResult.Error.Message}")));
                return AskHumanResult.SubmitFailed;
            }

            return AskHumanResult.Handled;
        }

        lock (_gate)
        {
            // Exactly one console input owner.
            if (_pending is not null)
            {
                _diagnosticConsole.MarkupLine(
                    _palette.ErrorMarkup(Markup.Escape(
                        "ask_human: console input is already owned by another prompt.")));
                return AskHumanResult.SubmitFailed;
            }

            // A disowned read is still parked on the console. Starting a second one would put two
            // readers on the same keyboard, so this prompt is refused rather than answered wrongly.
            if (_disownedRead is { IsCompleted: false })
            {
                _diagnosticConsole.MarkupLine(
                    _palette.ErrorMarkup(Markup.Escape(
                        "ask_human: a previous prompt's console read is still waiting for input, so "
                        + "this prompt cannot be answered here.")));
                return AskHumanResult.SubmitFailed;
            }

            _disownedRead = null;

            int generation = ++_generation;
            TaskCompletionSource dismissTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingHitl pending = new(
                args.PromptId.Trim(),
                evt.ToolCall?.CallId,
                args.Question.Trim(),
                generation,
                dismissTcs);
            _pending = pending;
            _settledResult = null;
            _raceTask = RunInteractiveRaceAsync(pending, cancellationToken);
        }

        return AskHumanResult.PendingInput;
    }

    /// <summary>
    /// Observes stream frames that may dismiss a pending interactive prompt.
    /// Safe to call for every event; no-ops when nothing is pending.
    /// </summary>
    public void ObserveStreamEvent(IntelligenceEvent evt)
    {
        PendingHitl? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        if (pending is null)
        {
            return;
        }

        switch (evt.Type)
        {
            case IntelligenceEventType.ToolError:
            case IntelligenceEventType.ToolResult:
                if (!CallIdMatches(pending.CallId, evt.ToolCall?.CallId))
                {
                    // Timeout text on a matching ask_human result may omit CallId — check message.
                    if (evt.Type == IntelligenceEventType.ToolResult
                        && !string.IsNullOrWhiteSpace(evt.Data ?? evt.Message)
                        && (evt.Data ?? evt.Message)!.Contains(
                            HumanPromptTimeoutException.DefaultMessage,
                            StringComparison.Ordinal))
                    {
                        Dismiss(pending.Generation);
                    }

                    return;
                }

                Dismiss(pending.Generation);
                return;

            case IntelligenceEventType.Result:
            case IntelligenceEventType.Error:
                Dismiss(pending.Generation);
                return;

            default:
                return;
        }
    }

    public void Cancel()
    {
        PendingHitl? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        if (pending is not null)
        {
            Dismiss(pending.Generation);
        }
    }

    /// <summary>
    /// Waits for any in-flight interactive race to settle and drains abandoned input ownership.
    /// </summary>
    public async Task<AskHumanResult?> DrainAsync(CancellationToken cancellationToken = default)
    {
        Task? race;
        lock (_gate)
        {
            race = _raceTask;
        }

        if (race is not null)
        {
            try
            {
                await race.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancel();
                try
                {
                    // Bounded for the same reason the abandoned read is: a drain that outlives its
                    // own cancelled token is the wedge this method exists to end, not prevent.
                    await race.WaitAsync(AbandonedReadGrace).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        lock (_gate)
        {
            AskHumanResult? result = _settledResult;
            _settledResult = null;
            _raceTask = null;
            return result;
        }
    }

    private async Task RunInteractiveRaceAsync(PendingHitl pending, CancellationToken cancellationToken)
    {
        string promptMarkup =
            $"\n{_palette.HeadingBoldMarkup(Markup.Escape("Mage asks:"))} {Markup.Escape(pending.Question)} ";

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<string?> inputTask = _readLineAsync(promptMarkup, false, linked.Token);
        Task dismissTask = pending.DismissTcs.Task;

        try
        {
            Task completed = await Task.WhenAny(inputTask, dismissTask).ConfigureAwait(false);

            if (completed == dismissTask || pending.DismissTcs.Task.IsCompleted)
            {
                // Prompt/turn ended — never submit. Drain abandoned ReadLine so it cannot steal REPL.
                linked.Cancel();
                await DisownInputAsync(inputTask).ConfigureAwait(false);
                Settle(pending.Generation, AskHumanResult.Handled);
                return;
            }

            string? answer;
            try
            {
                answer = await inputTask.ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                _diagnosticConsole.MarkupLine(
                    _palette.ErrorMarkup(Markup.Escape(
                        "ask_human: no interactive input is available to answer the prompt.")));
                Settle(pending.Generation, AskHumanResult.SubmitFailed);
                return;
            }
            catch (OperationCanceledException)
            {
                // A read that ends cancelled while the caller's own token is still live is the
                // operator's Ctrl+C, taken as a keystroke rather than as SIGINT. Nothing else will
                // observe it: the caller is parked in its stream pump, and `linked` is a child of the
                // caller's token, so cancelling it here cannot reach the parent. Hand the interrupt
                // back instead — that is what makes the command unwind and return the documented 130.
                if (!cancellationToken.IsCancellationRequested)
                {
                    NotifyOperatorInterrupt();
                }

                Settle(pending.Generation, AskHumanResult.Handled);
                return;
            }

            // Re-check ownership before posting — never submit after prompt/turn ended.
            if (!StillOwns(pending.Generation))
            {
                Settle(pending.Generation, AskHumanResult.Handled);
                return;
            }

            if (string.IsNullOrWhiteSpace(answer))
            {
                _diagnosticConsole.MarkupLine(
                    _palette.ErrorMarkup(Markup.Escape(
                        "ask_human: no answer was provided; the prompt was left unanswered.")));
                Settle(pending.Generation, AskHumanResult.SubmitFailed);
                return;
            }

            lock (_gate)
            {
                if (_pending is null
                    || _pending.Generation != pending.Generation
                    || _pending.SubmitStarted)
                {
                    Settle(pending.Generation, AskHumanResult.Handled);
                    return;
                }

                _pending.SubmitStarted = true;
            }

            Result<bool> submitResult;
            try
            {
                submitResult = await _apiClient
                    .SubmitHumanResponseAsync(pending.PromptId, answer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Settle(pending.Generation, AskHumanResult.Handled);
                return;
            }

            if (submitResult.IsFailure)
            {
                _diagnosticConsole.MarkupLine(
                    _palette.ErrorMarkup(Markup.Escape(
                        $"Failed to submit response to Daemon ({submitResult.Error.Code}): {submitResult.Error.Message}")));
                Settle(pending.Generation, AskHumanResult.SubmitFailed);
                return;
            }

            Settle(pending.Generation, AskHumanResult.Handled);
        }
        catch (Exception)
        {
            Settle(pending.Generation, AskHumanResult.SubmitFailed);
            throw;
        }
    }

    /// <summary>
    /// Reports the operator's interrupt to the caller. A callback that throws must not become the
    /// race's own failure — the prompt is already over, and the caller has one more chance to notice
    /// the interrupt when its stream ends.
    /// </summary>
    private void NotifyOperatorInterrupt()
    {
        try
        {
            _onOperatorInterrupt?.Invoke();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Dismiss(int generation)
    {
        PendingHitl? pending;
        lock (_gate)
        {
            pending = _pending;
            if (pending is null || pending.Generation != generation)
            {
                return;
            }
        }

        _ = pending.DismissTcs.TrySetResult();
    }

    private bool StillOwns(int generation)
    {
        lock (_gate)
        {
            return _pending is not null
                && _pending.Generation == generation
                && !_pending.DismissTcs.Task.IsCompleted;
        }
    }

    private void Settle(int generation, AskHumanResult result)
    {
        lock (_gate)
        {
            if (_pending is null || _pending.Generation != generation)
            {
                return;
            }

            _settledResult = result;
            _pending = null;
        }
    }

    private static bool CallIdMatches(string? pendingCallId, string? eventCallId)
    {
        if (string.IsNullOrWhiteSpace(pendingCallId) || string.IsNullOrWhiteSpace(eventCallId))
        {
            return false;
        }

        return string.Equals(pendingCallId, eventCallId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gives an abandoned read <see cref="AbandonedReadGrace" /> to notice the cancellation, then
    /// disowns it. Whatever the read eventually returns is discarded either way — the generation and
    /// ownership checks already forbid submitting it — so the only thing the wait buys is releasing
    /// the console before the next prompt, and only a reader that can observe its token ever does.
    /// </summary>
    private async Task DisownInputAsync(Task<string?> inputTask)
    {
        try
        {
            _ = await inputTask.WaitAsync(AbandonedReadGrace).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            // The read cannot observe its token. Fall through and disown it.
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        lock (_gate)
        {
            _disownedRead = inputTask;
        }

        // Nothing awaits this task again, so its failure would otherwise go unobserved.
        _ = inputTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static IAnsiConsole CreateStandardErrorConsole() =>
        AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(Console.Error),
            });

    private static Task<string?> DefaultReadLineAsync(
        string promptMarkup,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        // The reader polls for a keystroke rather than blocking in Console.ReadKey, so it observes the
        // token throughout the read and a dismissed prompt gives the console back on its own.
        // Draining after dismiss stays as the backstop for a console that cannot report readiness.
        bool empty = allowEmpty;
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CliLineReader.ReadLine(promptMarkup, empty, cancellationToken);
            },
            CancellationToken.None);
    }
}
