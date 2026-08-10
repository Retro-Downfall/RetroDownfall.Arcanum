using System.Collections.Concurrent;

using A2A;

using A2ATaskStatus = A2A.TaskStatus;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Default <see cref="IA2AClientService"/> (the Archmage Client). Discovers remote Agent Cards, validates
/// outbound URLs against <see cref="OutboundUrlGuard"/> and the optional <c>AllowedRemoteAgents</c> allowlist,
/// and performs a blocking A2A message exchange until completion or caller/host cancellation.
/// </summary>
/// <remarks>
/// Concurrency is governed by an in-memory <see cref="SemaphoreSlim"/> sized from the retained
/// host-capacity policy at first use. Excess work waits behind that admission boundary until a slot
/// opens or its caller cancels; the capacity is not a total-work rejection rule.
/// </remarks>
public sealed class A2AClientService : IA2AClientService
{

    public const string OutboundHttpClientName = "A2AOutbound";

    /// <summary>
    /// Bound on establishing the TCP connection to a remote agent. This is connection setup only — a
    /// dispatched Sending itself has no whole-operation deadline and ends on completion, remote failure,
    /// or caller/host cancellation (issue #55).
    /// </summary>
    public static readonly TimeSpan OutboundConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default header the peer credential is sent in — Arcanum's own API-key header.</summary>
    public const string DefaultOutboundCredentialHeader = "X-Arcanum-Key";

    private const string WellKnownAgentCardPath = "/.well-known/agent-card.json";

    /// <summary>
    /// Bound on the Agent Card cache. Keys come from model-supplied URLs, so this is a memory guard on a
    /// derived index — not a limit on how many distinct agents may be dispatched to.
    /// </summary>
    private const int MaxCachedCards = 256;

    private static readonly TimeSpan CardCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan InitialPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Bound on the best-effort peer-cancel call issued after the caller's token is already cancelled.</summary>
    private static readonly TimeSpan RemoteCancelTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly IOptionsMonitor<ArcanumSettings> _options;

    private readonly ILogger<A2AClientService> _logger;

    private readonly ConcurrentDictionary<string, CachedCard> _cardCache = new(StringComparer.Ordinal);

    private readonly Lazy<SemaphoreSlim> _gate;

    /// <summary>
    /// Reaches the scoped durable ledger from this singleton. Optional: a host without one (tests, a
    /// Grimoire-less CLI path) simply keeps no durable record, which is the pre-#62 behavior.
    /// </summary>
    private readonly IServiceScopeFactory? _scopeFactory;

    /// <summary>
    /// Live index of Sendings awaiting a peer callback. Optional: without it, callback mode degrades to
    /// the blocking path rather than waiting on a wake-up nothing can deliver (issue #67).
    /// </summary>
    private readonly A2ASendingCallbackRegistry? _callbacks;

    public A2AClientService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<ArcanumSettings> options,
        ILogger<A2AClientService> logger,
        IServiceScopeFactory? scopeFactory = null,
        A2ASendingCallbackRegistry? callbacks = null)
    {
        _callbacks = callbacks;

        _httpClientFactory = httpClientFactory;

        _options = options;

        _logger = logger;

        _scopeFactory = scopeFactory;

        _gate = new Lazy<SemaphoreSlim>(() =>
        {
            int max = ArcanumSettingClamps.MaxConcurrentA2ATasks(
                _options.CurrentValue.Execution.MaxConcurrentA2ATasks);

            return new SemaphoreSlim(max, max);
        });

    }

    public Task<Result<A2ADispatchResult>> DispatchSendingAsync(
        string goal,
        string? name,
        string agentUrl,
        IReadOnlyList<string>? delegationChain = null,
        CancellationToken cancellationToken = default,
        IProgress<A2ASendingProgress>? progress = null,
        A2ADispatchMode mode = A2ADispatchMode.Blocking,
        A2ASendingOptions? options = null)
    {

        if (string.IsNullOrWhiteSpace(goal))
        {

            return Task.FromResult(Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Apprentice.InvalidGoal, "A non-empty goal is required to dispatch a Sending.")));

        }

        return RunGuardedAsync(
            agentUrl,
            delegationChain,
            cancellationToken,
            (client, card, url, chain, slot, ct) => DispatchInternalAsync(
                client,
                card,
                goal.Trim(),
                url,
                chain,
                progress,
                mode,
                options,
                slot,
                ct));

    }

    public Task<Result<A2ADispatchResult>> ContinueSendingAsync(
        string agentUrl,
        string taskId,
        string message,
        IReadOnlyList<string>? delegationChain = null,
        CancellationToken cancellationToken = default,
        IProgress<A2ASendingProgress>? progress = null,
        A2ADispatchMode mode = A2ADispatchMode.Blocking,
        A2ASendingOptions? options = null)
    {

        if (string.IsNullOrWhiteSpace(taskId))
        {

            return Task.FromResult(Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.TaskRejected, "A non-empty remote task id is required to continue a Sending.")));

        }

        if (string.IsNullOrWhiteSpace(message))
        {

            return Task.FromResult(Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Apprentice.InvalidGoal, "A non-empty message is required to continue a Sending.")));

        }

        return RunGuardedAsync(
            agentUrl,
            delegationChain,
            cancellationToken,
            (client, card, url, chain, slot, ct) => ContinueInternalAsync(
                client,
                card,
                url,
                taskId.Trim(),
                message.Trim(),
                chain,
                progress,
                mode,
                options,
                slot,
                ct));

    }

    public async Task<Result> CancelRemoteTaskAsync(
        string agentUrl,
        string taskId,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(taskId))
        {

            return Result.Failure(new Error(ErrorCodes.Sending.TaskRejected, "A non-empty remote task id is required."));

        }

        Result<A2ADispatchResult> outcome = await RunGuardedAsync(
                agentUrl,
                delegationChain: null,
                cancellationToken,
                async (client, card, url, chain, slot, ct) =>
                {

                    try
                    {

                        await client
                            .CancelTaskAsync(new CancelTaskRequest { Id = taskId.Trim() }, ct)
                            .ConfigureAwait(false);

                        return Result<A2ADispatchResult>.Success(
                            new A2ADispatchResult(taskId.Trim(), "canceled"));

                    }
                    catch (Exception ex) when (ex is HttpRequestException or A2AException)
                    {

                        return Result<A2ADispatchResult>.Failure(new Error(
                            ErrorCodes.Sending.AgentUnreachable,
                            $"Could not cancel remote task '{taskId}': {ex.Message}"));

                    }

                })
            .ConfigureAwait(false);

        return outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error);

    }

    /// <summary>
    /// Shared preflight for every outbound call: feature gate, allowlist, SSRF guard, Agent Card
    /// resolution and interface validation, credential scoping, and the concurrency admission gate.
    /// </summary>
    private async Task<Result<A2ADispatchResult>> RunGuardedAsync(
        string agentUrl,
        IReadOnlyList<string>? delegationChain,
        CancellationToken cancellationToken,
        Func<IA2AClient, AgentCard, string, IReadOnlyList<string>, ConcurrencySlot, CancellationToken, Task<Result<A2ADispatchResult>>> operation)
    {

        ArcanumSettings settings = _options.CurrentValue;

        ConclaveA2ASettings a2a = settings.ResolveA2A();

        if (!settings.ResolveConclave().Enabled || !a2a.Enabled || !a2a.ClientEnabled)
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.Disabled, "A2A is disabled; dispatch_sending is not available."));

        }

        if (string.IsNullOrWhiteSpace(agentUrl) || !Uri.TryCreate(agentUrl.Trim(), UriKind.Absolute, out _))
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentCardInvalid, "A non-empty absolute agent_url is required to dispatch a Sending."));

        }

        string trimmedUrl = agentUrl.Trim();

        string[] allowlist = a2a.AllowedRemoteAgents ?? [];

        if (allowlist.Length > 0 && !IsAllowedAgent(trimmedUrl, allowlist))
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentNotAllowed, $"Remote agent '{trimmedUrl}' is not in the configured AllowedRemoteAgents allowlist."));

        }

        Result discoveryUrlCheck = await OutboundUrlGuard
            .ValidateUntrustedUrlAsync(trimmedUrl, cancellationToken)
            .ConfigureAwait(false);

        if (discoveryUrlCheck.IsFailure)
        {

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentUnreachable, $"Remote agent URL rejected by outbound URL policy: {discoveryUrlCheck.Error.Message}"));

        }

        SemaphoreSlim gate = _gate.Value;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        ConcurrencySlot slot = new(gate);

        try
        {

            Result<ConnectedPeer> peer = await ConnectAsync(trimmedUrl, allowlist, cancellationToken)
                .ConfigureAwait(false);

            if (peer.IsFailure)
            {

                return Result<A2ADispatchResult>.Failure(peer.Error);

            }

            return await operation(
                    peer.Value.Client,
                    peer.Value.Card,
                    trimmedUrl,
                    delegationChain ?? [],
                    slot,
                    cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            // Released here regardless of success, remote failure, or caller/host cancellation — and
            // idempotently, because a callback-mode Sending gives the slot back the moment the peer has
            // somewhere to report to (issue #67).
            slot.Release();

        }

    }

    /// <summary>
    /// One outbound Sending's claim on <c>MaxConcurrentA2ATasks</c>, releasable exactly once.
    /// </summary>
    /// <remarks>
    /// The gate bounds simultaneous <em>work</em>. A callback-mode Sending waiting for a peer is not
    /// work, so it hands the slot back early — but the guarded scope still releases in its <c>finally</c>,
    /// and double-releasing a <see cref="SemaphoreSlim"/> would silently raise the concurrency ceiling.
    /// </remarks>
    private sealed class ConcurrencySlot(SemaphoreSlim gate)
    {

        private int _released;

        public void Release()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                gate.Release();

            }

        }

    }

    private sealed record ConnectedPeer(IA2AClient Client, AgentCard Card);

    private async Task<Result<ConnectedPeer>> ConnectAsync(
        string discoveryUrl,
        string[] allowlist,
        CancellationToken cancellationToken)
    {

        AgentCard card;

        try
        {

            card = await ResolveCardAsync(discoveryUrl, allowlist, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is HttpRequestException or A2AException or InvalidOperationException)
        {

            _logger.LogWarning(ex, "dispatch_sending: failed to resolve Agent Card at {AgentUrl}.", discoveryUrl);

            return Result<ConnectedPeer>.Failure(
                new Error(ErrorCodes.Sending.AgentCardInvalid, $"Could not resolve the remote agent's Agent Card: {ex.Message}"));

        }

        AgentInterface[] interfaces = [.. (card.SupportedInterfaces ?? []).Where(static i => !string.IsNullOrWhiteSpace(i.Url))];

        if (interfaces.Length == 0)
        {

            return Result<ConnectedPeer>.Failure(
                new Error(ErrorCodes.Sending.AgentCardInvalid, "The remote Agent Card did not advertise a usable interface."));

        }

        // EVERY advertised interface is checked, not just the first: A2AClientFactory selects by protocol
        // binding preference rather than position, so validating one entry would let a hostile card steer
        // the connection to an unchecked URL. All of these are remote-controlled values.
        foreach (AgentInterface advertised in interfaces)
        {

            string interfaceUrl = advertised.Url!;

            if (allowlist.Length > 0 && !IsAllowedAgent(interfaceUrl, allowlist))
            {

                return Result<ConnectedPeer>.Failure(
                    new Error(ErrorCodes.Sending.AgentNotAllowed, $"Remote agent interface '{interfaceUrl}' is not in the configured AllowedRemoteAgents allowlist."));

            }

            Result interfaceUrlCheck = await OutboundUrlGuard
                .ValidateUntrustedUrlAsync(interfaceUrl, cancellationToken)
                .ConfigureAwait(false);

            if (interfaceUrlCheck.IsFailure)
            {

                return Result<ConnectedPeer>.Failure(
                    new Error(ErrorCodes.Sending.AgentUnreachable, $"Remote agent interface rejected by outbound URL policy: {interfaceUrlCheck.Error.Message}"));

            }

        }

        HttpClient httpClient = CreateOutboundClient(CredentialTargetForCard(card, discoveryUrl, allowlist), allowlist);

        IA2AClient client;

        try
        {

            client = A2AClientFactory.Create(card, httpClient);

        }
        catch (Exception ex) when (ex is A2AException or ArgumentException or InvalidOperationException)
        {

            return Result<ConnectedPeer>.Failure(
                new Error(ErrorCodes.Sending.AgentCardInvalid, $"The remote Agent Card advertises no protocol binding this client supports: {ex.Message}"));

        }

        return Result<ConnectedPeer>.Success(new ConnectedPeer(client, card));

    }

    private Task<Result<A2ADispatchResult>> DispatchInternalAsync(
        IA2AClient client,
        AgentCard card,
        string goal,
        string discoveryUrl,
        IReadOnlyList<string> delegationChain,
        IProgress<A2ASendingProgress>? progress,
        A2ADispatchMode mode,
        A2ASendingOptions? options,
        ConcurrencySlot slot,
        CancellationToken cancellationToken)
    {

        Result<A2AOutboundModality> modality = NegotiateAsync(card, options);

        if (modality.IsFailure)
        {

            return Task.FromResult(Result<A2ADispatchResult>.Failure(modality.Error));

        }

        Message message = new()
        {
            Role = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
            Parts = [Part.FromText(goal)],
        };

        // Loop prevention: the receiving agent refuses work whose chain already contains its own node.
        ConclaveDelegationChain.Write(message, ConclaveDelegationChain.Extend(delegationChain));

        return ExchangeAsync(
            client,
            card,
            message,
            discoveryUrl,
            progress,
            mode,
            modality.Value,
            options?.BudgetReservationId,
            slot,
            cancellationToken);

    }

    private Task<Result<A2ADispatchResult>> ContinueInternalAsync(
        IA2AClient client,
        AgentCard card,
        string discoveryUrl,
        string taskId,
        string followUp,
        IReadOnlyList<string> delegationChain,
        IProgress<A2ASendingProgress>? progress,
        A2ADispatchMode mode,
        A2ASendingOptions? options,
        ConcurrencySlot slot,
        CancellationToken cancellationToken)
    {

        Result<A2AOutboundModality> modality = NegotiateAsync(card, options);

        if (modality.IsFailure)
        {

            return Task.FromResult(Result<A2ADispatchResult>.Failure(modality.Error));

        }

        // TaskId is what makes this a continuation rather than a second task: the peer routes it to the
        // waiting task instead of minting a new one, which is what an escalated remote is waiting for
        // (issue #64).
        Message message = new()
        {
            Role = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            Parts = [Part.FromText(followUp)],
        };

        ConclaveDelegationChain.Write(message, ConclaveDelegationChain.Extend(delegationChain));

        return ExchangeAsync(
            client,
            card,
            message,
            discoveryUrl,
            progress,
            mode,
            modality.Value,
            options?.BudgetReservationId,
            slot,
            cancellationToken);

    }

    /// <summary>
    /// Runs the outbound modality/skill check against the resolved card and logs what was agreed.
    /// </summary>
    /// <remarks>
    /// Deliberately before <see cref="ExchangeAsync"/>: a mismatch discovered after <c>SendMessage</c>
    /// leaves a remote task created, running, and billing for an answer this instance cannot read
    /// (issue #65).
    /// </remarks>
    private Result<A2AOutboundModality> NegotiateAsync(AgentCard card, A2ASendingOptions? options)
    {

        Result<A2AOutboundModality> modality = A2AAgentCardPolicy.ValidateOutboundModes(
            _options.CurrentValue.ResolveA2A(),
            card,
            options);

        if (modality.IsFailure)
        {

            _logger.LogWarning(
                "dispatch_sending: refusing to dispatch — {Reason}",
                modality.Error.Message);

        }

        return modality;

    }

    /// <summary>
    /// Sends <paramref name="message"/>, then awaits the resulting remote task until it settles — by
    /// subscription when the peer advertises streaming, otherwise by polling — publishing a progress
    /// observation on every remote state change.
    /// </summary>
    private async Task<Result<A2ADispatchResult>> ExchangeAsync(
        IA2AClient client,
        AgentCard card,
        Message message,
        string discoveryUrl,
        IProgress<A2ASendingProgress>? progress,
        A2ADispatchMode mode,
        A2AOutboundModality modality,
        Guid? budgetReservationId,
        ConcurrencySlot slot,
        CancellationToken cancellationToken)
    {

        // ReturnImmediately hands back the remote task id before the work finishes. That id is what makes
        // local cancellation propagatable — a blocking send abandons the HTTP call without ever learning
        // which remote task to cancel, leaving the peer running and billing (issue #12).
        // AcceptedOutputModes states what this instance can read: leaving it unset let a peer answer in a
        // modality nothing on this side could consume (issue #65).
        SendMessageRequest sendRequest = new()
        {
            Message = message,
            Configuration = new SendMessageConfiguration
            {
                ReturnImmediately = true,
                AcceptedOutputModes = [.. modality.AcceptedOutputModes],
            },
        };

        SendMessageResponse response;

        DateTimeOffset dispatchedAt = DateTimeOffset.UtcNow;

        try
        {

            response = await client.SendMessageAsync(sendRequest, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is HttpRequestException or A2AException)
        {

            _logger.LogWarning(ex, "dispatch_sending: failed to send message to the remote agent.");

            return Result<A2ADispatchResult>.Failure(
                new Error(ErrorCodes.Sending.AgentUnreachable, $"Failed to send the Sending to the remote agent: {ex.Message}"));

        }

        if (response.PayloadCase == SendMessageResponseCase.Message)
        {

            // A stateless reply: no task was created, so there is nothing to poll and no task metadata to
            // read usage from. Unknown cost, not zero.
            return Result<A2ADispatchResult>.Success(new A2ADispatchResult(
                null,
                ExtractText(response.Message!.Parts),
                A2ARemoteCost.Unknown,
                dispatchedAt,
                DateTimeOffset.UtcNow));

        }

        AgentTask task = response.Task
            ?? throw new InvalidOperationException("A2A SendMessageResponse carried neither a Message nor a Task payload.");

        // One filter across both the subscription and the poll fallback: a stream that drops after
        // reporting `working` must not make the poll loop report `working` again (issue #61).
        TransitionFilter transitions = new(task.Status.State, StatusText(task.Status));

        Report(progress, discoveryUrl, task, dispatchedAt);

        // Durable from the moment the remote task id exists: a process that dies here used to leave a
        // remote task nobody could name, still running and still billing (issue #62). Written without the
        // caller's token — cancelling right here is exactly when the record matters most, and letting the
        // write cancel would skip the peer-cancel path below and orphan the remote task.
        A2ASendingLedgerEntry ledgerEntry = await RecordOutboundAsync(task.Id, discoveryUrl, budgetReservationId)
            .ConfigureAwait(false);

        // Callback mode asks the peer to report back, then stops occupying a slot while it works. A peer
        // that cannot (or an instance that has the surface off) simply keeps the slot and waits, which is
        // the historical behavior rather than a failure (issue #67).
        A2ACallbackSubscription? callback = mode == A2ADispatchMode.Callback
            ? await TryRegisterCallbackAsync(client, card, task.Id, ledgerEntry, cancellationToken).ConfigureAwait(false)
            : null;

        if (callback is not null)
        {

            slot.Release();

        }

        try
        {

            task = callback is null
                ? await AwaitSettledAsync(client, card, task, discoveryUrl, progress, transitions, cancellationToken)
                    .ConfigureAwait(false)
                : await AwaitCallbackAsync(client, task, discoveryUrl, progress, transitions, callback, cancellationToken)
                    .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            await TryCancelRemoteTaskAsync(client, task.Id).ConfigureAwait(false);

            await ReleaseLedgerAsync(ledgerEntry).ConfigureAwait(false);

            throw;

        }
        catch (Exception ex) when (ex is HttpRequestException or A2AException)
        {

            // The remote accepted the task and then the transport failed. The work may still be running
            // there, so report the task id rather than letting an exception escape as an opaque tool error.
            // The ledger entry deliberately stays open: reconciliation will try to cancel it.
            _logger.LogWarning(ex, "dispatch_sending: lost contact with the remote agent while polling task {TaskId}.", task.Id);

            return Result<A2ADispatchResult>.Failure(new Error(
                ErrorCodes.Sending.AgentUnreachable,
                $"Lost contact with the remote agent while awaiting task '{task.Id}': {ex.Message}. "
                + "The remote task may still be running; it was not cancelled."));

        }
        finally
        {

            callback?.Dispose();

        }

        DateTimeOffset settledAt = DateTimeOffset.UtcNow;

        // Read once, from the settled task: a peer that reports nothing stays explicitly unknown rather
        // than defaulting to a free Sending (issue #60).
        A2ARemoteCost cost = A2ASendingUsageMetadata.Read(task);

        // A settled task needs no reconciliation, whatever state it settled in — except a continuation,
        // which is deliberately left alive and therefore left recorded. Closing the record also stamps
        // what it cost, so the day's delegated spend is a durable figure rather than a live total that
        // dies with the process (issue #69).
        if (task.Status.State is not (TaskState.InputRequired or TaskState.AuthRequired)
            || mode != A2ADispatchMode.Continuable)
        {

            await SettleLedgerAsync(ledgerEntry, cost).ConfigureAwait(false);

        }

        if (task.Status.State == TaskState.Completed)
        {

            RecordSettled("completed", cost, dispatchedAt, settledAt);

            return Result<A2ADispatchResult>.Success(
                new A2ADispatchResult(task.Id, ExtractTaskText(task), cost, dispatchedAt, settledAt));

        }

        string reason = task.Status.Message is { } statusMessage
            ? ExtractText(statusMessage.Parts)
            : $"Remote task ended in state {task.Status.State}.";

        if (task.Status.State is TaskState.InputRequired or TaskState.AuthRequired)
        {

            A2AContinuationNeed need = task.Status.State == TaskState.InputRequired
                ? A2AContinuationNeed.Input
                : A2AContinuationNeed.Authentication;

            if (mode == A2ADispatchMode.Continuable)
            {

                RecordSettled("continuation", cost, dispatchedAt, settledAt);

                // The remote task stays alive so the Mage can answer it. Cancelling here is what forces a
                // whole re-run just to add one sentence of detail (issue #64).
                return Result<A2ADispatchResult>.Success(new A2ADispatchResult(
                    task.Id,
                    reason,
                    cost,
                    dispatchedAt,
                    settledAt,
                    new A2ASendingContinuation(task.Id, need, reason)));

            }

            // Neither state is terminal in A2A, but a blocking Sending has no way to supply the follow-up
            // input or credential, so waiting is an infinite poll that also pins a concurrency slot. End it
            // with a reason the Mage can act on, and point at the continuable mode that can answer it.
            await TryCancelRemoteTaskAsync(client, task.Id).ConfigureAwait(false);

            string wanted = need == A2AContinuationNeed.Input
                ? "asked for more input"
                : "asked for authentication";

            RecordSettled("failed", cost, dispatchedAt, settledAt);

            return Result<A2ADispatchResult>.Failure(new Error(
                ErrorCodes.Sending.TaskRejected,
                $"The remote agent {wanted} before it could finish, which a blocking Sending cannot supply: {reason} "
                + "Re-dispatch with --continuable to answer it instead of ending the Sending."));

        }

        RecordSettled("failed", cost, dispatchedAt, settledAt);

        return Result<A2ADispatchResult>.Failure(new Error(ErrorCodes.Sending.TaskRejected, reason));

    }

    /// <summary>A live outbound Sending's callback registration, and the wake-up it waits on.</summary>
    private sealed class A2ACallbackSubscription(string configId, SemaphoreSlim signal, IDisposable registration)
        : IDisposable
    {

        public string ConfigId => configId;

        public SemaphoreSlim Signal => signal;

        public void Dispose()
        {

            registration.Dispose();

            signal.Dispose();

        }

    }

    /// <summary>
    /// Asks the peer to post task transitions back to this instance, so the Sending can stop holding a
    /// concurrency slot while the remote works.
    /// </summary>
    /// <returns>
    /// The live registration, or <c>null</c> when callback mode is unavailable for this pairing — the
    /// caller then keeps its slot and waits exactly as a blocking Sending does.
    /// </returns>
    /// <remarks>
    /// Three things must all be true: this instance has the surface enabled and a reachable callback base
    /// URL, and the peer advertises <c>pushNotifications</c>. Peer capability decides, as it does for
    /// streaming (issue #66) — an instance cannot configure a peer into supporting a protocol method.
    /// </remarks>
    private async Task<A2ACallbackSubscription?> TryRegisterCallbackAsync(
        IA2AClient client,
        AgentCard card,
        string remoteTaskId,
        A2ASendingLedgerEntry ledgerEntry,
        CancellationToken cancellationToken)
    {

        ConclaveA2ASettings a2a = _options.CurrentValue.ResolveA2A();

        if (_callbacks is null
            || !a2a.PushNotificationsEnabled
            || string.IsNullOrWhiteSpace(a2a.PushCallbackBaseUrl)
            || card.Capabilities?.PushNotifications != true)
        {

            _logger.LogDebug(
                "dispatch_sending: callback mode is unavailable for remote task {TaskId}; waiting inline instead.",
                remoteTaskId);

            return null;

        }

        string configId = Guid.NewGuid().ToString("N");

        string token = A2ACallbackToken.Mint();

        string callbackUrl =
            $"{a2a.PushCallbackBaseUrl.TrimEnd('/')}{ResolveCallbackPath(a2a)}/{configId}";

        try
        {

            await client
                .CreateTaskPushNotificationConfigAsync(
                    new CreateTaskPushNotificationConfigRequest
                    {
                        TaskId = remoteTaskId,
                        ConfigId = configId,
                        Config = new PushNotificationConfig
                        {
                            Id = configId,
                            Url = callbackUrl,
                            Token = token,
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is HttpRequestException or A2AException)
        {

            _logger.LogInformation(
                ex,
                "dispatch_sending: the peer would not register a callback for remote task {TaskId}; "
                + "waiting inline instead.",
                remoteTaskId);

            return null;

        }

        // Durable before the slot is released: a callback that arrives in a later process has only this
        // record to recognise it by, and by then the live index is gone.
        await RecordCallbackAsync(ledgerEntry, configId, A2ACallbackToken.Hash(token)).ConfigureAwait(false);

        SemaphoreSlim signal = new(0);

        return new A2ACallbackSubscription(
            configId,
            signal,
            _callbacks.Register(configId, A2ACallbackToken.Hash(token), signal));

    }

    /// <summary>The absolute path peers post outbound-Sending callbacks to.</summary>
    internal static string ResolveCallbackPath(ConclaveA2ASettings a2a)
    {

        string configured = string.IsNullOrWhiteSpace(a2a.ServerPath)
            ? ArcanumRuntimeDefaults.Conclave.A2A.ServerPath
            : a2a.ServerPath.Trim();

        string normalized = configured.StartsWith("/api", StringComparison.Ordinal)
            ? configured
            : $"/api/{configured.Trim('/')}";

        return $"{normalized.TrimEnd('/')}/callbacks";

    }

    /// <summary>
    /// Waits for the peer to report back, re-reading the task on each notification until it settles.
    /// </summary>
    /// <remarks>
    /// The notification body is deliberately not trusted as the outcome: it is a remote-authored claim
    /// about state, and the authoritative answer — including artifacts and the usage block a Sending's
    /// cost is read from — comes from <c>tasks/get</c>. A callback that never arrives leaves the Sending
    /// waiting, which is the same "no whole-operation deadline" contract every other mode has (#55);
    /// what it does <em>not</em> do is hold a concurrency slot while it waits.
    /// </remarks>
    private static async Task<AgentTask> AwaitCallbackAsync(
        IA2AClient client,
        AgentTask task,
        string discoveryUrl,
        IProgress<A2ASendingProgress>? progress,
        TransitionFilter transitions,
        A2ACallbackSubscription callback,
        CancellationToken cancellationToken)
    {

        while (!IsSettled(task.Status.State))
        {

            await callback.Signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            task = await client
                .GetTaskAsync(new GetTaskRequest { Id = task.Id }, cancellationToken)
                .ConfigureAwait(false);

            if (transitions.ShouldReport(task.Status))
            {

                Report(progress, discoveryUrl, task, DateTimeOffset.UtcNow);

            }

        }

        return task;

    }

    private async Task RecordCallbackAsync(A2ASendingLedgerEntry entry, string configId, string tokenHash)
    {

        if (!entry.IsRecorded || _scopeFactory is null)
        {

            return;

        }

        try
        {

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            if (A2ASendingLedgerScope.Resolve(scope.ServiceProvider) is { } ledger)
            {

                await ledger
                    .RecordOutboundCallbackAsync(entry, configId, tokenHash, CancellationToken.None)
                    .ConfigureAwait(false);

            }

        }
        catch (Exception ex)
        {

            _logger.LogWarning(ex, "dispatch_sending: could not record a Sending's callback registration.");

        }

    }

    private async Task<A2ASendingLedgerEntry> RecordOutboundAsync(
        string remoteTaskId,
        string agentUrl,
        Guid? budgetReservationId)
    {

        if (_scopeFactory is null)
        {

            return default;

        }

        try
        {

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IA2ASendingLedger? ledger = A2ASendingLedgerScope.Resolve(scope.ServiceProvider);

            return ledger is null
                ? default
                : await ledger
                    .RegisterOutboundAsync(remoteTaskId, agentUrl, budgetReservationId, CancellationToken.None)
                    .ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            _logger.LogWarning(ex, "dispatch_sending: could not record a durable Sending for remote task {TaskId}.", remoteTaskId);

            return default;

        }

    }

    /// <summary>
    /// Closes a durable record once its Sending settles. Runs without the caller's token: a cancelled
    /// Sending still needs its record closed, or reconciliation chases a task that is already stopped.
    /// </summary>
    private Task ReleaseLedgerAsync(A2ASendingLedgerEntry entry) =>
        CloseLedgerAsync(entry, cost: null);

    /// <summary>
    /// Closes a settled Sending's durable record, stamping what the peer said it cost — including that
    /// it said nothing (issue #69).
    /// </summary>
    private Task SettleLedgerAsync(A2ASendingLedgerEntry entry, A2ARemoteCost cost) =>
        CloseLedgerAsync(entry, cost);

    /// <summary>
    /// Closes a durable record once its Sending settles. Runs without the caller's token: a cancelled
    /// Sending still needs its record closed, or reconciliation chases a task that is already stopped.
    /// </summary>
    private async Task CloseLedgerAsync(A2ASendingLedgerEntry entry, A2ARemoteCost? cost)
    {

        if (!entry.IsRecorded || _scopeFactory is null)
        {

            return;

        }

        try
        {

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IA2ASendingLedger? ledger = A2ASendingLedgerScope.Resolve(scope.ServiceProvider);

            if (ledger is null)
            {

                return;

            }

            if (cost is null)
            {

                await ledger.ReleaseAsync(entry, CancellationToken.None).ConfigureAwait(false);

                return;

            }

            await ledger.SettleOutboundAsync(entry, cost, CancellationToken.None).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            _logger.LogWarning(ex, "dispatch_sending: could not close the durable record for a settled Sending.");

        }

    }

    /// <summary>
    /// Records what a settled Sending cost, including the case where nobody said.
    /// </summary>
    /// <remarks>
    /// The <c>cost=unknown</c> series exists so delegated work with no reported price shows up as
    /// <em>unpriced</em> instead of disappearing into the totals at zero (issue #60). The peer's URL is
    /// never a label: allowlist entries can name private partner endpoints (DESIGN &#167;5.7.1).
    /// </remarks>
    private static void RecordSettled(
        string outcome,
        A2ARemoteCost cost,
        DateTimeOffset dispatchedAt,
        DateTimeOffset settledAt)
    {

        KeyValuePair<string, object?> outcomeTag = new("outcome", outcome);

        ArcanumMetrics.ConclaveSendingsTotal.Add(
            1,
            outcomeTag,
            new KeyValuePair<string, object?>("cost", cost.IsKnown ? "known" : "unknown"));

        if (cost.TotalTokens is { } tokens)
        {

            ArcanumMetrics.ConclaveSendingRemoteTokensTotal.Add(tokens, outcomeTag);

        }

        if (cost.CostUsd is { } usd)
        {

            ArcanumMetrics.ConclaveSendingRemoteCostUsdTotal.Add((double)usd, outcomeTag);

        }

        if (settledAt >= dispatchedAt && dispatchedAt != default)
        {

            ArcanumMetrics.ConclaveSendingDuration.Record((settledAt - dispatchedAt).TotalSeconds, outcomeTag);

        }

    }

    /// <summary>
    /// Awaits the remote task until it settles, preferring the peer's own push stream over polling.
    /// </summary>
    /// <remarks>
    /// <strong>Peer capability decides, not configuration</strong> (issue #66): a card that advertises
    /// <c>Capabilities.Streaming</c> is subscribed to, and anything else is polled exactly as before. The
    /// subscription is best-effort in both directions — a peer that advertises streaming and then refuses
    /// it, or a stream that ends mid-Sending, degrades to the poll loop rather than failing a Sending that
    /// is still running perfectly well on the far side.
    /// </remarks>
    private async Task<AgentTask> AwaitSettledAsync(
        IA2AClient client,
        AgentCard card,
        AgentTask task,
        string discoveryUrl,
        IProgress<A2ASendingProgress>? progress,
        TransitionFilter transitions,
        CancellationToken cancellationToken)
    {

        if (card.Capabilities?.Streaming == true)
        {

            AgentTask? streamed = await TrySubscribeUntilSettledAsync(
                    client,
                    task,
                    discoveryUrl,
                    progress,
                    transitions,
                    cancellationToken)
                .ConfigureAwait(false);

            if (streamed is not null)
            {

                return streamed;

            }

        }

        return await PollUntilSettledAsync(client, task, discoveryUrl, progress, transitions, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Consumes the peer's <c>tasks/subscribe</c> stream until the task settles.
    /// </summary>
    /// <returns>
    /// The settled task, or <c>null</c> when the subscription could not carry the Sending to a settled
    /// state — the caller then falls back to polling.
    /// </returns>
    /// <remarks>
    /// The settled task is re-read once through <c>tasks/get</c>. A status-update frame carries the state
    /// but not the task's artifacts or metadata, and those are where the response text and the peer's
    /// usage block live — reading once keeps settle semantics byte-identical to the poll path instead of
    /// making a streamed Sending quietly lose its answer and its cost.
    /// <para>
    /// A task that already reached a terminal state before the subscription opened is refused by the
    /// protocol, which is exactly the fast-remote race; that refusal lands here as a fallback to polling,
    /// whose first read finds the settled task immediately.
    /// </para>
    /// </remarks>
    private async Task<AgentTask?> TrySubscribeUntilSettledAsync(
        IA2AClient client,
        AgentTask task,
        string discoveryUrl,
        IProgress<A2ASendingProgress>? progress,
        TransitionFilter transitions,
        CancellationToken cancellationToken)
    {

        try
        {

            IAsyncEnumerable<StreamResponse> stream = client.SubscribeToTaskAsync(
                new SubscribeToTaskRequest { Id = task.Id },
                cancellationToken);

            await foreach (StreamResponse update in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {

                if (ReadState(update) is not { } observed)
                {

                    continue;

                }

                if (transitions.ShouldReport(observed))
                {

                    Report(progress, discoveryUrl, task.Id, observed.State, DateTimeOffset.UtcNow);

                }

                if (IsSettled(observed.State))
                {

                    return await client
                        .GetTaskAsync(new GetTaskRequest { Id = task.Id }, cancellationToken)
                        .ConfigureAwait(false);

                }

            }

            _logger.LogInformation(
                "dispatch_sending: the push stream for remote task {TaskId} ended before it settled; falling back to polling.",
                task.Id);

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex) when (ex is HttpRequestException or A2AException or IOException or InvalidOperationException)
        {

            _logger.LogInformation(
                ex,
                "dispatch_sending: could not follow remote task {TaskId} by subscription; falling back to polling.",
                task.Id);

        }

        return null;

    }

    /// <summary>
    /// The task status a stream frame describes, or <c>null</c> for a frame that says nothing about state.
    /// </summary>
    private static A2ATaskStatus? ReadState(StreamResponse update) => update.PayloadCase switch
    {
        StreamResponseCase.Task => update.Task?.Status,
        StreamResponseCase.StatusUpdate => update.StatusUpdate?.Status,
        _ => null,
    };

    /// <summary>
    /// Polls the remote task until it settles: an A2A terminal state, or <c>input-required</c> /
    /// <c>auth-required</c>, which are not terminal but which a blocking Sending can never move past.
    /// </summary>
    /// <remarks>
    /// The delay backs off from <see cref="InitialPollInterval"/> to <see cref="MaxPollInterval"/> so a fast
    /// remote is observed promptly without hammering a slow one. There is no poll-count or elapsed ceiling:
    /// the Sending ends on a settled remote state or caller/host cancellation (issue #55).
    /// <para>
    /// <paramref name="progress"/> fires on remote <em>transitions</em>, not once per poll: a two-second
    /// backoff against a long remote task would otherwise flood the Chronicle with identical frames
    /// (issue #61). The filter is shared with the streaming path so a fallback does not re-announce a
    /// state the stream already reported.
    /// </para>
    /// </remarks>
    private static async Task<AgentTask> PollUntilSettledAsync(
        IA2AClient client,
        AgentTask task,
        string discoveryUrl,
        IProgress<A2ASendingProgress>? progress,
        TransitionFilter transitions,
        CancellationToken cancellationToken)
    {

        TimeSpan delay = InitialPollInterval;

        while (!IsSettled(task.Status.State))
        {

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            delay = delay < MaxPollInterval
                ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxPollInterval.Ticks))
                : MaxPollInterval;

            task = await client
                .GetTaskAsync(new GetTaskRequest { Id = task.Id }, cancellationToken)
                .ConfigureAwait(false);

            if (transitions.ShouldReport(task.Status))
            {

                Report(progress, discoveryUrl, task, DateTimeOffset.UtcNow);

            }

        }

        return task;

    }

    /// <summary>
    /// Remembers the last remote state observed, so both the push stream and the poll loop report
    /// <em>transitions</em> rather than one frame per delivery.
    /// </summary>
    private sealed class TransitionFilter(TaskState state, string statusText)
    {

        private TaskState _state = state;

        private string _statusText = statusText;

        public bool ShouldReport(A2ATaskStatus status)
        {

            string text = StatusText(status);

            if (status.State == _state && string.Equals(text, _statusText, StringComparison.Ordinal))
            {

                return false;

            }

            _state = status.State;

            _statusText = text;

            return true;

        }

    }

    private static void Report(
        IProgress<A2ASendingProgress>? progress,
        string discoveryUrl,
        AgentTask task,
        DateTimeOffset observedAt) =>
        Report(progress, discoveryUrl, task.Id, task.Status.State, observedAt);

    private static void Report(
        IProgress<A2ASendingProgress>? progress,
        string discoveryUrl,
        string taskId,
        TaskState state,
        DateTimeOffset observedAt) =>
        progress?.Report(new A2ASendingProgress(
            discoveryUrl,
            taskId,
            StateName(state),
            A2ASendingDirection.Outbound,
            observedAt));

    /// <summary>
    /// The peer's status prose, used only to notice that something changed. It never leaves this method:
    /// remote-authored text can echo the delegated prompt back and a Chronicle frame is an operator
    /// surface, not a data channel (issue #61).
    /// </summary>
    private static string StatusText(A2ATaskStatus status) =>
        status.Message is { } message ? ExtractText(message.Parts) : string.Empty;

    /// <summary>A2A wire spelling of a task state, so Chronicle frames match the protocol vocabulary.</summary>
    internal static string StateName(TaskState state) => state switch
    {
        TaskState.Submitted => "submitted",
        TaskState.Working => "working",
        TaskState.Completed => "completed",
        TaskState.Failed => "failed",
        TaskState.Canceled => "canceled",
        TaskState.InputRequired => "input-required",
        TaskState.Rejected => "rejected",
        TaskState.AuthRequired => "auth-required",
        _ => "unspecified",
    };

    private static bool IsSettled(TaskState state) =>
        TaskStateExtensions.IsTerminal(state)
        || state is TaskState.InputRequired or TaskState.AuthRequired;

    /// <summary>
    /// Best-effort peer cancellation. Runs on its own short-lived token because the caller's token is
    /// already cancelled, and never rethrows: the local operation is ending either way, and a remote that
    /// refuses or has already finished is not a local failure.
    /// </summary>
    private async Task TryCancelRemoteTaskAsync(IA2AClient client, string taskId)
    {

        using CancellationTokenSource cleanupScope = new(RemoteCancelTimeout);

        try
        {

            await client
                .CancelTaskAsync(new CancelTaskRequest { Id = taskId }, cleanupScope.Token)
                .ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            _logger.LogInformation(
                ex,
                "dispatch_sending: could not cancel remote A2A task {TaskId} after local cancellation.",
                taskId);

        }

    }

    /// <summary>
    /// Decides whether the peer credential may travel to the interfaces a remote Agent Card advertises.
    /// </summary>
    /// <returns>
    /// A representative target URL when <em>every</em> advertised interface shares the discovery origin or is
    /// explicitly allowlisted; otherwise <c>null</c>, meaning "dispatch without a credential".
    /// </returns>
    /// <remarks>
    /// The card is remote-controlled. A hostile peer that advertises an interface on a host it also controls
    /// would otherwise be handed the operator's credential — the SSRF guard and allowlist stop us connecting
    /// to a <em>blocked</em> host, but with the default empty allowlist any public host is reachable. The
    /// check covers every interface because <c>A2AClientFactory</c> selects one by protocol-binding
    /// preference rather than position, so a per-interface decision could be made about a URL the SDK does
    /// not end up using.
    /// </remarks>
    private static string? CredentialTargetForCard(AgentCard card, string discoveryUrl, string[] allowlist)
    {

        if (!Uri.TryCreate(discoveryUrl, UriKind.Absolute, out Uri? discovery))
        {

            return null;

        }

        string discoveryOrigin = discovery.GetLeftPart(UriPartial.Authority);

        string? representative = null;

        foreach (AgentInterface advertised in card.SupportedInterfaces ?? [])
        {

            if (string.IsNullOrWhiteSpace(advertised.Url))
            {

                continue;

            }

            if (!Uri.TryCreate(advertised.Url, UriKind.Absolute, out Uri? target))
            {

                return null;

            }

            bool vouchedFor = string.Equals(
                    target.GetLeftPart(UriPartial.Authority),
                    discoveryOrigin,
                    StringComparison.OrdinalIgnoreCase)
                || (allowlist.Length > 0 && IsAllowedAgent(advertised.Url, allowlist));

            if (!vouchedFor)
            {

                return null;

            }

            representative ??= advertised.Url;

        }

        return representative;

    }

    /// <summary>
    /// Resolves the remote Agent Card, accepting either an explicit card URL or a base URL.
    /// </summary>
    /// <remarks>
    /// An explicit path (<c>https://peer/api/conclave/a2a/agent-card</c>) is fetched exactly as given. A
    /// bare origin falls back to the SDK's well-known path and then to Arcanum's own default card path,
    /// because Arcanum deliberately serves no unauthenticated <c>/.well-known/agent-card.json</c> — probing
    /// only the well-known path made one Arcanum unable to discover another at all (issue #12).
    /// </remarks>
    private async Task<AgentCard> ResolveCardAsync(
        string discoveryUrl,
        string[] allowlist,
        CancellationToken cancellationToken)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_cardCache.TryGetValue(discoveryUrl, out CachedCard? cached) && cached.ExpiresAt > now)
        {

            return cached.Card;

        }

        Uri uri = new(discoveryUrl);

        string origin = uri.GetLeftPart(UriPartial.Authority);

        string path = uri.PathAndQuery;

        string[] candidatePaths = path.Length > 1
            ? [path]
            : [WellKnownAgentCardPath, ArcanumRuntimeDefaults.Conclave.A2A.ServerPath + "/agent-card"];

        // agent_url is model-supplied on dispatch_sending, so discovery is credentialed only when the
        // allowlist vouches for it. CreateOutboundClient owns that decision for every caller.
        HttpClient httpClient = CreateOutboundClient(discoveryUrl, allowlist);

        Exception? lastFailure = null;

        foreach (string candidate in candidatePaths)
        {

            try
            {

                A2ACardResolver resolver = new(new Uri(origin), httpClient, candidate);

                AgentCard card = await resolver.GetAgentCardAsync(cancellationToken).ConfigureAwait(false);

                CacheCard(discoveryUrl, card, now);

                return card;

            }
            catch (Exception ex) when (ex is HttpRequestException or A2AException)
            {

                lastFailure = ex;

            }

        }

        throw lastFailure ?? new A2AException("The remote agent did not serve an Agent Card.");

    }

    /// <summary>
    /// Stores a resolved card and sweeps expired entries. The cache is keyed on a model-supplied URL, so it
    /// is bounded rather than left to grow with every distinct string an Apprentice dispatches to.
    /// </summary>
    private void CacheCard(string discoveryUrl, AgentCard card, DateTimeOffset now)
    {

        if (_cardCache.Count >= MaxCachedCards)
        {

            foreach (KeyValuePair<string, CachedCard> entry in _cardCache)
            {

                if (entry.Value.ExpiresAt <= now)
                {

                    _cardCache.TryRemove(entry.Key, out _);

                }

            }

            // Still full of live entries: drop the soonest-to-expire so the cache cannot grow without bound.
            while (_cardCache.Count >= MaxCachedCards)
            {

                KeyValuePair<string, CachedCard> oldest = _cardCache
                    .OrderBy(static e => e.Value.ExpiresAt)
                    .FirstOrDefault();

                if (oldest.Key is null || !_cardCache.TryRemove(oldest.Key, out _))
                {

                    break;

                }

            }

        }

        _cardCache[discoveryUrl] = new CachedCard(card, now.Add(CardCacheTtl));

    }

    /// <summary>
    /// Builds the outbound client, attaching the operator-configured peer credential only when the target
    /// matches a non-empty <c>Arcanum:Integrations:A2A:AllowedRemoteAgents</c> entry. The credential is read
    /// from the environment at dispatch time and never stored in configuration.
    /// </summary>
    /// <param name="credentialTarget">
    /// The URL this client will talk to, or <c>null</c> for a client that carries no credential.
    /// </param>
    /// <param name="allowlist">The configured <c>AllowedRemoteAgents</c> entries.</param>
    /// <remarks>
    /// The allowlist is the <em>only</em> thing that vouches for a credential target, and the rule is uniform
    /// across callers. <c>agent_url</c> reaches this service verbatim from the model on the
    /// <c>dispatch_sending</c> / <c>continue_sending</c> tools, so "the operator typed this URL" was never a
    /// safe assumption: a prompt-injected Apprentice could otherwise name an attacker host and be handed a
    /// peer Arcanum API key — an operator-equivalent credential (&#167;11.13) — during card discovery, before
    /// any card-interface scoping runs. When nothing vouches for the target the Sending still goes out, just
    /// unauthenticated.
    /// </remarks>
    private HttpClient CreateOutboundClient(string? credentialTarget, string[] allowlist)
    {

        HttpClient httpClient = _httpClientFactory.CreateClient(OutboundHttpClientName);

        ConclaveA2ASettings a2a = _options.CurrentValue.ResolveA2A();

        if (credentialTarget is null || string.IsNullOrWhiteSpace(a2a.OutboundCredentialEnvironmentVariable))
        {

            return httpClient;

        }

        if (allowlist.Length == 0 || !IsAllowedAgent(credentialTarget, allowlist))
        {

            _logger.LogWarning(
                "dispatch_sending: withholding the outbound peer credential from '{AgentUrl}' because it "
                + "matches no Arcanum:Integrations:A2A:AllowedRemoteAgents entry. The Sending is dispatched "
                + "unauthenticated; add the target to that setting to authenticate it.",
                credentialTarget);

            return httpClient;

        }

        string? credential = System.Environment.GetEnvironmentVariable(
            a2a.OutboundCredentialEnvironmentVariable.Trim());

        if (string.IsNullOrWhiteSpace(credential))
        {

            _logger.LogWarning(
                "dispatch_sending: Arcanum:Integrations:A2A:OutboundCredentialEnvironmentVariable names "
                + "'{EnvironmentVariable}', but it is not set; dispatching without a peer credential.",
                a2a.OutboundCredentialEnvironmentVariable);

            return httpClient;

        }

        string header = string.IsNullOrWhiteSpace(a2a.OutboundCredentialHeader)
            ? DefaultOutboundCredentialHeader
            : a2a.OutboundCredentialHeader.Trim();

        httpClient.DefaultRequestHeaders.Remove(header);

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header, credential);

        return httpClient;

    }

    private static bool IsAllowedAgent(string url, string[] allowlist)
    {

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {

            return false;

        }

        foreach (string entry in allowlist)
        {

            if (string.IsNullOrWhiteSpace(entry))
            {

                continue;

            }

            string trimmed = entry.Trim();

            if (string.Equals(trimmed, url, StringComparison.OrdinalIgnoreCase))
            {

                return true;

            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? allowedUri)
                && string.Equals(
                    allowedUri.GetLeftPart(UriPartial.Authority),
                    uri.GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase))
            {

                return true;

            }

        }

        return false;

    }

    private static string ExtractText(IReadOnlyList<Part>? parts)
    {

        if (parts is null || parts.Count == 0)
        {

            return string.Empty;

        }

        IEnumerable<string> textParts = parts
            .Where(static p => p.ContentCase == PartContentCase.Text && !string.IsNullOrEmpty(p.Text))
            .Select(static p => p.Text!);

        return string.Join('\n', textParts);

    }

    private static string ExtractTaskText(AgentTask task)
    {

        Artifact? lastArtifact = task.Artifacts?.LastOrDefault();

        if (lastArtifact is { Parts.Count: > 0 })
        {

            string artifactText = ExtractText(lastArtifact.Parts);

            if (!string.IsNullOrEmpty(artifactText))
            {

                return artifactText;

            }

        }

        if (task.Status.Message is { } statusMessage)
        {

            string statusText = ExtractText(statusMessage.Parts);

            if (!string.IsNullOrEmpty(statusText))
            {

                return statusText;

            }

        }

        return "(The remote agent completed the Sending without a textual response.)";

    }

    private sealed record CachedCard(AgentCard Card, DateTimeOffset ExpiresAt);

}
