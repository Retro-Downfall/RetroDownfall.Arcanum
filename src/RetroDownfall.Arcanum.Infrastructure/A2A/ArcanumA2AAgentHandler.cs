using System.Collections.Concurrent;

using A2A;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// The A2A server side of the Conclave: implements the SDK's <see cref="IAgentHandler"/> so external A2A
/// clients can drive Arcanum Apprentices. An inbound message spawns a headless Apprentice via the same
/// <see cref="IConclaveArchmage"/> used by <c>cast_sending</c> and <c>POST /api/apprentices/{id}/cast</c>;
/// the Apprentice's own Chronicle is then forwarded onto the A2A task lifecycle until a terminal state.
/// </summary>
/// <remarks>
/// A2A tasks map to Apprentices, not Sessions: the task lifecycle IS the Apprentice lifecycle, so this
/// class does no independent scheduling — it starts the Apprentice via <see cref="IApprenticeRuntime"/> and
/// then simply relays <see cref="IApprenticeRuntime.SubscribeChronicleAsync"/>. <see cref="_taskToApprentice"/>
/// is the live index and dies with the process; the association itself is recorded durably through
/// <see cref="IA2ASendingLedger"/>, so a peer that cancels after a restart still reaches the real
/// Apprentice (issue #62, docs/Arcanum.DESIGN.md &#167;5.7.1.2).
/// </remarks>
public sealed class ArcanumA2AAgentHandler(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ArcanumSettings> options,
    ILogger<ArcanumA2AAgentHandler> logger,
    A2APushNotificationRegistry? pushNotifications = null,
    A2APushNotificationDispatcher? pushDispatcher = null) : IAgentHandler, IAsyncDisposable
{

    /// <summary>
    /// How many parked continuations this process keeps on the fast path.
    /// </summary>
    /// <remarks>
    /// Nothing but an answer or a cancel removes an entry, so a peer that escalates and then does
    /// neither leaves one behind for the life of the process — the one A2A index that was not bounded
    /// while <see cref="ArcanumA2ATaskStore.RetainedTaskCap"/> and the push-notification registry's
    /// ceiling both were. Eviction costs the fast path only: the same park is recorded durably through
    /// <see cref="IA2ASendingLedger"/>, so an evicted task's answer still reaches its Apprentice by the
    /// route a restart uses (issue #68).
    /// </remarks>
    internal const int MaxParkedContinuations = 512;

    private readonly ConcurrentDictionary<string, Guid> _taskToApprentice = new(StringComparer.Ordinal);

    /// <summary>
    /// Tasks parked at <c>input-required</c> whose Apprentice is <c>Escalated</c> and still resumable.
    /// </summary>
    /// <remarks>
    /// Escalation ends <see cref="ExecuteAsync"/>, which drops the live mapping. Without this second
    /// index a peer that answers the question gets a brand-new Apprentice with none of the first one's
    /// plan, session, or progress — which is not a continuation at all (issue #64). This index is still
    /// process memory; the same fact is recorded durably through <see cref="IA2ASendingLedger"/> so the
    /// answer also lands after a restart (issue #68).
    /// </remarks>
    private readonly ConcurrentDictionary<string, ParkedContinuation> _awaitingContinuation = new(StringComparer.Ordinal);

    /// <summary>Insertion order, so the ceiling evicts the least recently parked task.</summary>
    private long _parkSequence;

    /// <summary>A parked Sending plus the arrival order the retention ceiling ranks it by.</summary>
    private readonly record struct ParkedContinuation(A2AParkedSending Sending, long Sequence);

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {

        TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

        ArcanumSettings settings = options.CurrentValue;

        ConclaveA2ASettings a2a = settings.ResolveA2A();

        if (!settings.ResolveConclave().Enabled || !a2a.Enabled || !a2a.ServerEnabled)
        {

            await RejectAsync(updater, "A2A is disabled on this Arcanum instance.", cancellationToken).ConfigureAwait(false);

            return;

        }

        string goal = context.UserText?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(goal))
        {

            await RejectAsync(updater, "A non-empty message is required to spawn an Apprentice.", cancellationToken).ConfigureAwait(false);

            return;

        }

        Result<string> modality = A2AAgentCardPolicy.ValidateRequestedModes(settings, context.Configuration?.AcceptedOutputModes);

        if (modality.IsFailure)
        {

            // An unsupported modality is refused by name rather than silently answered as text: a peer
            // that asked for audio and received prose has no way to tell that it was downgraded
            // (issue #63).
            await RejectAsync(updater, modality.Error.Message, cancellationToken).ConfigureAwait(false);

            return;

        }

        // A callback the peer supplied on the send itself, rather than through tasks/pushNotificationConfig.
        // Registered before any work starts so even an early rejection is reported (issue #67).
        await TryRegisterPushCallbackAsync(context, cancellationToken).ConfigureAwait(false);

        // The live index answers a continuation inside the process that parked it; the durable record
        // answers one that arrives after a restart. Without the second lookup the peer's answer falls
        // through to the normal path and mints a fresh Apprentice — the exact failure #64 removed, just
        // displaced past a restart boundary (issue #68).
        A2AParkedSending? parked = _awaitingContinuation.TryGetValue(context.TaskId, out ParkedContinuation live)
            ? live.Sending
            : await TryRecoverParkedAsync(context.TaskId, cancellationToken).ConfigureAwait(false);

        if (parked is { } awaiting)
        {

            await ContinueApprenticeAsync(context, updater, awaiting, goal, cancellationToken)
                .ConfigureAwait(false);

            return;

        }

        string[] inboundChain = ConclaveDelegationChain.Read(context.Message?.Metadata);

        if (ConclaveDelegationChain.ContainsSelf(inboundChain))
        {

            logger.LogWarning(
                "A2A: refusing task {TaskId} because its delegation chain already passed through this instance.",
                context.TaskId);

            await RejectAsync(
                updater,
                "This Sending has already been handled by this Arcanum instance; accepting it would create a delegation cycle.",
                cancellationToken).ConfigureAwait(false);

            return;

        }

        Result<string> workspaceResult = ResolveWorkspace(settings, a2a);

        if (workspaceResult.IsFailure)
        {

            await RejectAsync(updater, $"No usable workspace is configured: {workspaceResult.Error.Message}", cancellationToken).ConfigureAwait(false);

            return;

        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IConclaveArchmage archmage = scope.ServiceProvider.GetRequiredService<IConclaveArchmage>();

        Result<Apprentice> castResult = await archmage
            .CastAsync(
                new ConclaveCastRequest(
                    goal,
                    WorkspacePath: workspaceResult.Value,
                    DelegationChain: ConclaveDelegationChain.Extend(inboundChain)),
                cancellationToken)
            .ConfigureAwait(false);

        if (castResult.IsFailure)
        {

            await RejectAsync(updater, $"Could not spawn an Apprentice: {castResult.Error.Message}", cancellationToken).ConfigureAwait(false);

            return;

        }

        Apprentice apprentice = castResult.Value;

        _taskToApprentice[context.TaskId] = apprentice.Id;

        // The in-memory index dies with the process; the durable record is what lets a peer's
        // tasks/cancel after a restart reach the real Apprentice instead of answering into the void
        // (issue #62).
        A2ASendingLedgerEntry ledgerEntry = await RecordInboundAsync(
                scope.ServiceProvider,
                context.TaskId,
                apprentice.Id)
            .ConfigureAwait(false);

        await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

        IApprenticeRuntime runtime = scope.ServiceProvider.GetRequiredService<IApprenticeRuntime>();

        // The Chronicle is a live-only hub with no replay: an Apprentice that reaches a terminal state
        // before this subscription exists would strand the A2A task in Working forever. Begin enumerating
        // first — the first MoveNextAsync registers the subscriber — and only then start the Apprentice.
        IAsyncEnumerator<ApprenticeEvent> chronicle = runtime
            .SubscribeChronicleAsync(apprentice.Id, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        ValueTask<bool> firstEvent = chronicle.MoveNextAsync();

        try
        {

            Result<string> startResult = await runtime.StartAsync(apprentice.Id, cancellationToken).ConfigureAwait(false);

            if (startResult.IsFailure)
            {

                await FailAsync(updater, $"Apprentice '{apprentice.Id}' was created but could not be started: {startResult.Error.Message}", cancellationToken)
                    .ConfigureAwait(false);

                return;

            }

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await ForwardChronicleToTaskAsync(
                    context.TaskId,
                    context.ContextId,
                    apprentice.Id,
                    ledgerEntry,
                    scope.ServiceProvider,
                    chronicle,
                    firstEvent,
                    updater,
                    cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            _taskToApprentice.TryRemove(context.TaskId, out _);

            // A task parked awaiting continuation is still in flight, so its durable record stays open.
            if (!_awaitingContinuation.ContainsKey(context.TaskId))
            {

                await ReleaseInboundAsync(scope.ServiceProvider, ledgerEntry).ConfigureAwait(false);

            }

            await chronicle.DisposeAsync().ConfigureAwait(false);

        }

    }

    /// <summary>
    /// Holds the parked-continuation index at <see cref="MaxParkedContinuations"/> by dropping the least
    /// recently parked task.
    /// </summary>
    /// <remarks>
    /// The durable record is deliberately left open: it is what makes an evicted park answerable at all,
    /// and dropping the fast-path entry only sends the peer's answer through
    /// <see cref="TryRecoverParkedAsync"/> — the route a restart already uses. Closing it here would turn
    /// a memory bound into lost work.
    /// </remarks>
    private void EvictSurplusParks()
    {

        while (_awaitingContinuation.Count > MaxParkedContinuations)
        {

            KeyValuePair<string, ParkedContinuation> oldest = _awaitingContinuation
                .OrderBy(static entry => entry.Value.Sequence)
                .FirstOrDefault();

            if (oldest.Key is null || !_awaitingContinuation.TryRemove(oldest.Key, out _))
            {

                return;

            }

            logger.LogInformation(
                "A2A: parked task {TaskId} left the in-process continuation index at its {Cap}-entry "
                + "ceiling; an answer for it now resolves through its durable Sending record.",
                oldest.Key,
                MaxParkedContinuations);

        }

    }

    /// <summary>
    /// Resolves a parked Sending recorded by this or a previous process.
    /// </summary>
    /// <remarks>
    /// Best-effort and non-throwing, like every other ledger read on this path: when nothing is recorded
    /// the caller proceeds exactly as it did before, minting a fresh Apprentice rather than failing.
    /// </remarks>
    private async Task<A2AParkedSending?> TryRecoverParkedAsync(string taskId, CancellationToken cancellationToken)
    {

        try
        {

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            if (A2ASendingLedgerScope.Resolve(scope.ServiceProvider) is not { } ledger)
            {

                return null;

            }

            return await ledger
                .FindParkedInboundAsync(taskId, takeLease: true, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(ex, "A2A: could not look up a parked Sending for task {TaskId}.", taskId);

            return null;

        }

    }

    /// <summary>
    /// Records the durable correspondence. Deliberately untokened and non-throwing: the record matters
    /// most exactly when this request is being torn down, and a ledger failure must never turn into an
    /// unhandled exception from an <see cref="IAgentHandler"/> callback or a leaked live-index entry.
    /// </summary>
    private static async Task<A2ASendingLedgerEntry> RecordInboundAsync(
        IServiceProvider services,
        string taskId,
        Guid apprenticeId)
    {

        try
        {

            IA2ASendingLedger? ledger = A2ASendingLedgerScope.Resolve(services);

            return ledger is null
                ? default
                : await ledger.RegisterInboundAsync(taskId, apprenticeId, CancellationToken.None).ConfigureAwait(false);

        }
        catch (Exception)
        {

            // Best-effort; see remarks. A2A stays usable without the durable record.
            return default;

        }

    }

    private static async Task ReleaseInboundAsync(IServiceProvider services, A2ASendingLedgerEntry entry)
    {

        if (!entry.IsRecorded)
        {

            return;

        }

        if (A2ASendingLedgerScope.Resolve(services) is { } ledger)
        {

            await ledger.ReleaseAsync(entry, CancellationToken.None).ConfigureAwait(false);

        }

    }

    /// <summary>
    /// Stamps the durable record as parked so a continuation after a restart can find it.
    /// </summary>
    /// <remarks>
    /// Untokened and non-throwing for the same reason <see cref="RecordInboundAsync"/> is: this runs while
    /// the request is being torn down by the escalation, and a ledger failure must never escape an
    /// <see cref="IAgentHandler"/> callback or block the <c>input-required</c> transition the peer is owed.
    /// </remarks>
    private static async Task MarkParkedAsync(
        IServiceProvider services,
        A2ASendingLedgerEntry entry,
        string? contextId)
    {

        if (!entry.IsRecorded)
        {

            return;

        }

        try
        {

            if (A2ASendingLedgerScope.Resolve(services) is { } ledger)
            {

                await ledger.MarkParkedAsync(entry, contextId, CancellationToken.None).ConfigureAwait(false);

            }

        }
        catch (Exception)
        {

            // Best-effort; see remarks.

        }

    }

    /// <summary>
    /// Resumes an escalated Apprentice with the peer's answer through Divine Intervention and re-attaches
    /// the task relay, instead of minting a second Apprentice that knows nothing of the first (issue #64).
    /// </summary>
    private async Task ContinueApprenticeAsync(
        RequestContext context,
        TaskUpdater updater,
        A2AParkedSending parked,
        string guidance,
        CancellationToken cancellationToken)
    {

        Guid apprenticeId = parked.ApprenticeId;

        // Publish the live mapping before retiring the park, never the other way round: CancelAsync reads
        // the park first and the live index second, so writing them in this order leaves no instant in
        // which a concurrent cancel sees neither and falls through to the durable lookup — which, for a
        // Sending whose best-effort ledger write never landed, would answer the peer "Canceled" while
        // leaving this Apprentice running.
        _taskToApprentice[context.TaskId] = apprenticeId;

        _awaitingContinuation.TryRemove(context.TaskId, out _);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRuntime runtime = scope.ServiceProvider.GetRequiredService<IApprenticeRuntime>();

        IAsyncEnumerator<ApprenticeEvent> chronicle = runtime
            .SubscribeChronicleAsync(apprenticeId, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        ValueTask<bool> firstEvent = chronicle.MoveNextAsync();

        try
        {

            Result<string> intervened = await runtime
                .InterveneAsync(apprenticeId, guidance, resume: true, cancellationToken)
                .ConfigureAwait(false);

            if (intervened.IsFailure)
            {

                await FailAsync(
                    updater,
                    $"The Apprentice could not be resumed with the supplied answer: {intervened.Error.Message}",
                    cancellationToken).ConfigureAwait(false);

                return;

            }

            // Submit before Work even though the task already exists: the SDK opens this request's
            // response stream on the first Submit, and a continuation that skips it produces no events
            // for the peer at all.
            await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

            await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            await ForwardChronicleToTaskAsync(
                    context.TaskId,
                    context.ContextId,
                    apprenticeId,
                    parked.Ledger,
                    scope.ServiceProvider,
                    chronicle,
                    firstEvent,
                    updater,
                    cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            _taskToApprentice.TryRemove(context.TaskId, out _);

            // The record opened by the original ExecuteAsync stayed open across the park; close it now
            // unless this continuation parked the task again.
            if (!_awaitingContinuation.ContainsKey(context.TaskId))
            {

                await ReleaseInboundAsync(scope.ServiceProvider, parked.Ledger).ConfigureAwait(false);

            }

            await chronicle.DisposeAsync().ConfigureAwait(false);

        }

    }

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        // A task parked at input-required is still ours to stop: the peer giving up on the question must
        // cancel the Apprentice waiting for it, not just abandon the mapping (issue #64). This index is
        // read before the live one because the escalation publishes the park while the relay's live
        // mapping is still installed — the durable park write and the input-required transition both sit
        // inside that window. A park in hand therefore always means the relay has already committed to
        // returning, whatever the live index still says, so this path owes the peer the terminal
        // transition; reading the two in the order they are written would let a cancel in that window
        // fall through every branch and answer nothing at all.
        bool needsTerminalTransition = _awaitingContinuation.TryRemove(context.TaskId, out ParkedContinuation parked);

        Guid apprenticeId = parked.Sending.ApprenticeId;

        if (needsTerminalTransition)
        {

            await ReleaseInboundAsync(scope.ServiceProvider, parked.Sending.Ledger).ConfigureAwait(false);

        }

        // Only the live index means an ExecuteAsync relay is still enumerating this Apprentice's Chronicle
        // and will drive the terminal transition off ApprenticeCancelled. Knowing which Apprentice serves
        // the task is a different fact: every other path below has to emit that transition itself.
        bool live = !needsTerminalTransition && _taskToApprentice.TryGetValue(context.TaskId, out apprenticeId);

        if (!live && !needsTerminalTransition)
        {

            // The in-memory index dies with the process, but the durable record does not: a peer that
            // cancels after a restart must stop the resumed Apprentice, not merely be told "Canceled"
            // while the work keeps running (issue #62).
            Guid? recorded = A2ASendingLedgerScope.Resolve(scope.ServiceProvider) is { } ledger
                ? await ledger.FindInboundApprenticeAsync(context.TaskId, cancellationToken).ConfigureAwait(false)
                : null;

            if (recorded is { } recoveredId)
            {

                apprenticeId = recoveredId;

                needsTerminalTransition = true;

            }

        }

        if (!live && !needsTerminalTransition)
        {

            // Overriding IAgentHandler.CancelAsync replaces the SDK's own Canceled transition, so returning
            // silently would leave the peer's task with no terminal state at all. Nothing local claims this
            // task — no live mapping and no durable record — but the peer is still owed an answer.
            logger.LogInformation(
                "A2A CancelAsync: task {TaskId} has no live or recorded Apprentice mapping; answering Canceled.",
                context.TaskId);

            await RunTerminalTransitionAsync(
                ct => new TaskUpdater(eventQueue, context.TaskId, context.ContextId).CancelAsync(ct).AsTask(),
                cancellationToken).ConfigureAwait(false);

            await NotifyPeerAsync(context.TaskId, "canceled", cancellationToken).ConfigureAwait(false);

            return;

        }

        IApprenticeRuntime runtime = scope.ServiceProvider.GetRequiredService<IApprenticeRuntime>();

        // Best-effort: for a genuinely live task this triggers Apprentice cancellation, which ExecuteAsync's
        // own Chronicle subscription (still running as the SDK's background task for this same A2A task)
        // will observe as ApprenticeCancelled and use to drive the terminal TaskUpdater.CancelAsync
        // transition itself. In that case we do not call updater.CancelAsync here too, to avoid racing two
        // terminal-state transitions on the same task from two different call paths.
        Result<string> cancelResult = await runtime.CancelAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (cancelResult.IsFailure)
        {

            logger.LogInformation(
                "A2A CancelAsync: Apprentice {ApprenticeId} for task {TaskId} could not be cancelled: {Message}",
                apprenticeId,
                context.TaskId,
                cancelResult.Error.Message);

        }

        if (needsTerminalTransition)
        {

            // Parked at input-required, or recovered from the durable record after a restart: either way
            // no ExecuteAsync relay is running for this task, so nothing else will ever drive its terminal
            // transition. The peer would wait forever otherwise.
            await RunTerminalTransitionAsync(
                ct => new TaskUpdater(eventQueue, context.TaskId, context.ContextId).CancelAsync(ct).AsTask(),
                cancellationToken).ConfigureAwait(false);

            await NotifyPeerAsync(context.TaskId, "canceled", cancellationToken).ConfigureAwait(false);

        }

    }

    private async Task ForwardChronicleToTaskAsync(
        string taskId,
        string? contextId,
        Guid apprenticeId,
        A2ASendingLedgerEntry ledgerEntry,
        IServiceProvider services,
        IAsyncEnumerator<ApprenticeEvent> chronicle,
        ValueTask<bool> firstEvent,
        TaskUpdater updater,
        CancellationToken cancellationToken)
    {

        bool hasEvent = await firstEvent.ConfigureAwait(false);

        // Peer-visible usage, accumulated from provider-authoritative frames. A2A has no usage field, so
        // Arcanum publishes its own namespaced block; reporting nothing leaves the caller with an
        // explicit "unknown" rather than a fabricated zero (issue #60).
        long reportedTokens = 0;

        string? lastProgressLabel = null;

        while (hasEvent)
        {

            ApprenticeEvent @event = chronicle.Current;

            if (@event.WizardEvent?.Usage is { } usage)
            {

                reportedTokens += Math.Max(0, usage.TotalTokens);

            }

            switch (@event.Type)
            {

                case ApprenticeEventType.ApprenticeEscalated:

                    // The task is parked, not finished: the peer can answer it and resume this very
                    // Apprentice (issue #64). Its durable record stays open — the Sending has not settled —
                    // and is stamped parked so the answer still lands after a restart (issue #68).
                    _awaitingContinuation[taskId] = new ParkedContinuation(
                        new A2AParkedSending(apprenticeId, contextId, ledgerEntry),
                        Interlocked.Increment(ref _parkSequence));

                    EvictSurplusParks();

                    await MarkParkedAsync(services, ledgerEntry, contextId).ConfigureAwait(false);

                    await RequireInputAsync(
                        updater,
                        @event.Error ?? @event.Summary ?? "The Apprentice petitioned the Dungeon Master for guidance.",
                        cancellationToken).ConfigureAwait(false);

                    return;

                case ApprenticeEventType.ApprenticeCompleted:

                    string finalText = await ExtractFinalTextAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

                    await updater.AddArtifactAsync([Part.FromText(finalText)], cancellationToken: cancellationToken).ConfigureAwait(false);

                    await updater.CompleteAsync(BuildCompletionMessage(reportedTokens), cancellationToken).ConfigureAwait(false);

                    await NotifyPeerAsync(taskId, "completed", cancellationToken).ConfigureAwait(false);

                    return;

                case ApprenticeEventType.ApprenticeFailed:

                    await FailAsync(updater, @event.Error ?? "The Apprentice failed.", cancellationToken).ConfigureAwait(false);

                    return;

                case ApprenticeEventType.ApprenticeCancelled:

                    await updater.CancelAsync(cancellationToken).ConfigureAwait(false);

                    await NotifyPeerAsync(taskId, "canceled", cancellationToken).ConfigureAwait(false);

                    return;

                default:

                    // Step-level events have no distinct A2A TaskState, but a peer watching a long
                    // Sending should still see it moving rather than a single "Working" that never
                    // changes for an hour (issue #61). Only the phase label travels: step results, tool
                    // output, and Apprentice prose stay on this side of the door.
                    if (DescribeProgress(@event) is { } label
                        && !string.Equals(label, lastProgressLabel, StringComparison.Ordinal))
                    {

                        lastProgressLabel = label;

                        await RelayProgressAsync(updater, label, cancellationToken).ConfigureAwait(false);

                    }

                    break;

            }

            hasEvent = await chronicle.MoveNextAsync().ConfigureAwait(false);

        }

        // The Chronicle ended without a terminal Apprentice event — the peer is owed a definite answer
        // rather than a task left in Working until the process dies.
        await FailAsync(
            updater,
            "The Apprentice's Chronicle ended without a terminal result; the Sending cannot be completed.",
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// The peer-safe phase label for a step-level Chronicle event, or <c>null</c> for events that say
    /// nothing an external agent needs.
    /// </summary>
    /// <remarks>
    /// Deliberately structural: the event kind and a step number. Tool names, tool arguments, step
    /// results, and Ward reasons describe this instance's internals and its operator's workspace, and a
    /// remote agent has no business seeing them (docs/Arcanum.DESIGN.md &#167;5.7.1).
    /// </remarks>
    internal static string? DescribeProgress(ApprenticeEvent @event) => @event.Type switch
    {
        ApprenticeEventType.PlanGenerated => "Planning complete.",
        ApprenticeEventType.StepStarted => $"Working on step {StepLabel(@event)}.",
        ApprenticeEventType.StepCompleted => $"Completed step {StepLabel(@event)}.",
        ApprenticeEventType.StepRetrying => $"Retrying step {StepLabel(@event)}.",
        ApprenticeEventType.PlanRevised => "Plan revised.",
        ApprenticeEventType.ApprenticeIntervened => "Resumed with operator guidance.",
        ApprenticeEventType.ApprenticePaused => "Paused.",
        ApprenticeEventType.ApprenticeResumed => "Resumed.",
        _ => null,
    };

    private static string StepLabel(ApprenticeEvent @event) =>
        @event.StepIndex is { } index ? (index + 1).ToString() : "?";

    private static Task RelayProgressAsync(TaskUpdater updater, string label, CancellationToken cancellationToken) =>
        RunTerminalTransitionAsync(
            ct => updater.StartWorkAsync(BuildAgentMessage(label), ct).AsTask(),
            cancellationToken);

    /// <summary>
    /// Builds the terminal status message, carrying Arcanum's usage block when any was observed.
    /// </summary>
    /// <remarks>
    /// The A2A SDK's <c>TaskUpdater</c> exposes no way to write task-level metadata, so the block rides
    /// on the completion message, which is where <c>A2ASendingUsageMetadata.Read</c> also looks. A run
    /// that observed no provider-authoritative usage returns <c>null</c> and stays explicitly unknown.
    /// </remarks>
    private static Message? BuildCompletionMessage(long reportedTokens)
    {

        if (reportedTokens <= 0)
        {

            return null;

        }

        Message message = BuildAgentMessage("The Apprentice completed the delegated goal.");

        message.Metadata ??= [];

        A2ASendingUsageMetadata.Write(message.Metadata, reportedTokens, costUsd: null);

        return message;

    }

    private async Task<string> ExtractFinalTextAsync(Guid apprenticeId, CancellationToken cancellationToken)
    {

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository apprenticeRepo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await apprenticeRepo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice?.SessionId is not { } sessionId)
        {

            return "(The Apprentice completed without a recorded session.)";

        }

        ISessionRepository sessionRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

        List<Entry> entries = await sessionRepo.GetEntriesAscendingAsync(sessionId, takeLast: 20, cancellationToken).ConfigureAwait(false);

        Entry? lastAssistantEntry = entries.LastOrDefault(static e => e.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(e.Content));

        return lastAssistantEntry?.Content ?? "(The Apprentice completed without a textual final message.)";

    }

    /// <summary>
    /// Resolves the workspace an inbound A2A task will run in: <c>Arcanum:Integrations:A2A:DefaultWorkspace</c>
    /// → <c>Arcanum:Workspaces:DefaultRoot</c> → the process's current directory, each validated against
    /// <c>Arcanum:Security:CampaignRoots</c>.
    /// </summary>
    /// <remarks>
    /// Public so the health/meta Conclave projection reports exactly what an inbound task would get, instead
    /// of a second, drift-prone copy of this fallback chain (issue #12).
    /// </remarks>
    public static Result<string> ResolveWorkspace(ArcanumSettings settings, ConclaveA2ASettings a2a)
    {

        if (!string.IsNullOrWhiteSpace(a2a.DefaultWorkspace))
        {

            return CampaignPathPolicy.ValidateAndNormalizePath(a2a.DefaultWorkspace, settings);

        }

        string? defaultWorkspace = settings.ResolveDefaultWorkspace();

        if (!string.IsNullOrWhiteSpace(defaultWorkspace))
        {

            return CampaignPathPolicy.ValidateAndNormalizePath(
                defaultWorkspace,
                settings);

        }

        return CampaignPathPolicy.ValidateAndNormalizePath(Directory.GetCurrentDirectory(), settings);

    }

    /// <summary>
    /// Remembers a callback the peer supplied on the send itself, when the surface is enabled.
    /// </summary>
    /// <remarks>
    /// A refused callback (SSRF-blocked, not allowlisted, or the surface disabled) is logged and the
    /// Sending proceeds without notifications: the peer can always fetch the task state, and failing the
    /// delegated work over a webhook it optionally asked for would be a worse answer than not calling it.
    /// </remarks>
    private async Task TryRegisterPushCallbackAsync(RequestContext context, CancellationToken cancellationToken)
    {

        if (pushNotifications is null
            || context.Configuration?.PushNotificationConfig is not { } supplied
            || !pushNotifications.Enabled)
        {

            return;

        }

        Result<PushNotificationConfig> registered = await pushNotifications
            .RegisterAsync(context.TaskId, supplied, cancellationToken)
            .ConfigureAwait(false);

        if (registered.IsFailure)
        {

            logger.LogWarning(
                "A2A: task {TaskId} supplied a push-notification callback that was refused ({Reason}); "
                + "the Sending continues without notifications.",
                context.TaskId,
                registered.Error.Message);

        }

    }

    /// <summary>
    /// Posts a task-state transition to the callback its peer registered, if any.
    /// </summary>
    /// <remarks>
    /// Only the states a waiting peer can act on — the terminal ones and <c>input-required</c>. Relaying
    /// every step-level <c>working</c> update would turn a peer's webhook into the Chronicle firehose the
    /// progress de-duplication in &#167;5.7.1 exists to avoid.
    /// </remarks>
    private async Task NotifyPeerAsync(string taskId, string state, CancellationToken cancellationToken)
    {

        if (pushNotifications is null
            || pushDispatcher is null
            || !pushNotifications.Enabled
            || pushNotifications.Resolve(taskId) is not { } config)
        {

            return;

        }

        await pushDispatcher.NotifyAsync(config, taskId, state, cancellationToken).ConfigureAwait(false);

        if (state is not "input-required")
        {

            // A settled task has nothing further to report, so the callback is forgotten with it.
            pushNotifications.Remove(taskId);

        }

    }

    private async Task RejectAsync(TaskUpdater updater, string reason, CancellationToken cancellationToken)
    {

        await RunTerminalTransitionAsync(
                async ct =>
                {
                    await updater.SubmitAsync(ct).ConfigureAwait(false);

                    await updater.RejectAsync(BuildAgentMessage(reason), ct).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);

        await NotifyPeerAsync(updater.TaskId, "rejected", cancellationToken).ConfigureAwait(false);

    }

    private async Task FailAsync(TaskUpdater updater, string reason, CancellationToken cancellationToken)
    {

        await RunTerminalTransitionAsync(ct => updater.FailAsync(BuildAgentMessage(reason), ct).AsTask(), cancellationToken)
            .ConfigureAwait(false);

        await NotifyPeerAsync(updater.TaskId, "failed", cancellationToken).ConfigureAwait(false);

    }

    private async Task RequireInputAsync(TaskUpdater updater, string reason, CancellationToken cancellationToken)
    {

        await RunTerminalTransitionAsync(ct => updater.RequireInputAsync(BuildAgentMessage(reason), ct).AsTask(), cancellationToken)
            .ConfigureAwait(false);

        await NotifyPeerAsync(updater.TaskId, "input-required", cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Terminal <see cref="TaskUpdater"/> transitions are best-effort: if the task already reached a
    /// terminal state via another path (e.g. a concurrent cancel), the SDK may reject a second
    /// transition. That race is expected here and must never surface as an unhandled exception from an
    /// <see cref="IAgentHandler"/> callback.
    /// </summary>
    private static async Task RunTerminalTransitionAsync(Func<CancellationToken, Task> transition, CancellationToken cancellationToken)
    {

        try
        {

            await transition(cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception)
        {

            // Best-effort terminal transition; see remarks above.

        }

    }

    private static Message BuildAgentMessage(string text) => new()
    {
        Role = Role.Agent,
        MessageId = Guid.NewGuid().ToString("N"),
        Parts = [Part.FromText(text)],
    };

    public ValueTask DisposeAsync()
    {

        _taskToApprentice.Clear();

        _awaitingContinuation.Clear();

        return ValueTask.CompletedTask;

    }

}
