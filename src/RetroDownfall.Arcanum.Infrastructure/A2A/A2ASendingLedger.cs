using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>Which side of the door a durable Sending record describes.</summary>
public enum A2ASendingRecordDirection
{

    Inbound = 0,

    Outbound = 1,

}

/// <summary>
/// Durable record of an in-flight A2A correspondence, stored in the <c>LongRunningOperations</c> ledger.
/// </summary>
/// <remarks>
/// The runtime task-id map is process memory (DESIGN &#167;5.4.4). That was fine for the map itself, but it also
/// meant a restart lost the only record that a remote task existed: an inbound peer's <c>tasks/cancel</c>
/// hit nothing, and an outbound remote task nobody could name kept running and billing (issue #62). This
/// is that missing record — deliberately the shared ledger from #39 rather than a bespoke A2A table.
/// </remarks>
public sealed record A2ASendingRecord
{

    /// <summary>Checkpoint schema version. Bumped only on a breaking shape change.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = A2ASendingLedger.CheckpointVersion;

    [JsonPropertyName("direction")]
    public A2ASendingRecordDirection Direction { get; init; }

    /// <summary>Inbound: the peer's A2A task id. Outbound: the remote agent's task id.</summary>
    [JsonPropertyName("taskId")]
    public string TaskId { get; init; } = string.Empty;

    /// <summary>Inbound only: the Apprentice serving this task.</summary>
    [JsonPropertyName("apprenticeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ApprenticeId { get; init; }

    /// <summary>Outbound only: the discovery URL the Sending was dispatched to, needed to cancel it later.</summary>
    [JsonPropertyName("agentUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentUrl { get; init; }

    /// <summary>
    /// Inbound only: the Sending is parked at <c>input-required</c> and its escalated Apprentice is still
    /// waiting to be answered (issue #68).
    /// </summary>
    /// <remarks>
    /// Absent on a pre-#68 record, which deserializes as <c>false</c> — exactly the old behavior, so no
    /// checkpoint version bump is needed.
    /// </remarks>
    [JsonPropertyName("parked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Parked { get; init; }

    /// <summary>
    /// Inbound only: the A2A context id the task belongs to, needed to rebuild enough of the task for a
    /// peer's follow-up to route to it after a restart.
    /// </summary>
    [JsonPropertyName("contextId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContextId { get; init; }

    /// <summary>
    /// Outbound only: whether the peer reported what the settled Sending cost (issue #69).
    /// </summary>
    /// <remarks>
    /// <c>false</c> on a settled record means <em>unpriced</em>, not free. Nothing may read the absent
    /// <see cref="CostUsd"/> as zero — that is exactly the silent understatement #60 removed.
    /// </remarks>
    [JsonPropertyName("costKnown")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CostKnown { get; init; }

    /// <summary>Outbound only: peer-reported cost in USD, when it reported one.</summary>
    [JsonPropertyName("costUsd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CostUsd { get; init; }

    /// <summary>Outbound only: peer-reported total tokens, when it reported any.</summary>
    [JsonPropertyName("totalTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalTokens { get; init; }

    /// <summary>
    /// Outbound callback mode only: the push-notification config id this Sending registered with the
    /// peer, which is what a callback arriving in a later process is matched on (issue #67).
    /// </summary>
    [JsonPropertyName("callbackConfigId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallbackConfigId { get; init; }

    /// <summary>
    /// Outbound callback mode only: the SHA-256 digest of the per-Sending callback secret.
    /// </summary>
    /// <remarks>
    /// The digest, never the token: a durable record that carried a working callback credential would
    /// hand anyone who could read the Grimoire the ability to settle another instance's Sendings
    /// (&#167;11.2).
    /// </remarks>
    [JsonPropertyName("callbackTokenHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallbackTokenHash { get; init; }

}

/// <summary>
/// An escalated inbound Sending recovered from the durable ledger, and the handle that closes it.
/// </summary>
/// <param name="Ledger">
/// Recorded only when this process could take the record's lease. An unrecorded handle still resumes the
/// Apprentice — losing the ability to close the row is far cheaper than answering into the void.
/// </param>
public readonly record struct A2AParkedSending(
    Guid ApprenticeId,
    string? ContextId,
    A2ASendingLedgerEntry Ledger);

/// <summary>Handle on a durable Sending record, used to close it out when the Sending settles.</summary>
public readonly record struct A2ASendingLedgerEntry(Guid OperationId, string OwnerId)
{

    public bool IsRecorded => OperationId != Guid.Empty;

}

/// <summary>
/// Registers and resolves durable A2A task correspondences.
/// </summary>
public interface IA2ASendingLedger
{

    Task<A2ASendingLedgerEntry> RegisterInboundAsync(string taskId, Guid apprenticeId, CancellationToken cancellationToken = default);

    /// <param name="budgetReservationId">
    /// The reservation covering the turn that dispatched this Sending, when the caller has one. Linking
    /// it is what lets an operator see the delegated work a reservation paid for (issue #69).
    /// </param>
    Task<A2ASendingLedgerEntry> RegisterOutboundAsync(
        string remoteTaskId,
        string agentUrl,
        Guid? budgetReservationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what a settled outbound Sending cost — including that nobody said — and closes its row.
    /// </summary>
    Task SettleOutboundAsync(
        A2ASendingLedgerEntry entry,
        A2ARemoteCost cost,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a recorded Sending settled, so reconciliation ignores it after the next restart.</summary>
    Task ReleaseAsync(A2ASendingLedgerEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that an inbound Sending is parked at <c>input-required</c>, awaiting the peer's answer.
    /// </summary>
    /// <remarks>
    /// This is the fact that used to live only in process memory: the Apprentice was durable and was
    /// deliberately not auto-resumed, but nothing said which A2A task was waiting on it, so a
    /// continuation after a restart minted a second Apprentice instead (issue #68).
    /// </remarks>
    Task MarkParkedAsync(A2ASendingLedgerEntry entry, string? contextId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds an inbound Sending parked awaiting an answer.
    /// </summary>
    /// <param name="takeLease">
    /// Whether to claim the record for this process. <c>true</c> for a caller that will resume the
    /// Apprentice and must be able to close the row; <c>false</c> for a read — the task store looks a
    /// parked task up on every store miss, and claiming there would churn the row's attempt count for
    /// nothing.
    /// </param>
    Task<A2AParkedSending?> FindParkedInboundAsync(
        string taskId,
        bool takeLease = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the Apprentice serving an inbound A2A task, including one recorded by a previous process.
    /// </summary>
    Task<Guid?> FindInboundApprenticeAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the callback this outbound Sending registered with its peer, so a notification arriving
    /// in a later process can still be matched to the Sending it settles (issue #67).
    /// </summary>
    /// <param name="callbackTokenHash">The secret's digest. The secret itself is never persisted.</param>
    Task RecordOutboundCallbackAsync(
        A2ASendingLedgerEntry entry,
        string callbackConfigId,
        string callbackTokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the still-open outbound Sending a callback config id belongs to.
    /// </summary>
    Task<A2AOutboundCallback?> FindOutboundCallbackAsync(
        string callbackConfigId,
        CancellationToken cancellationToken = default);

}

/// <summary>
/// An outbound Sending awaiting a peer callback, recovered from the durable ledger.
/// </summary>
/// <param name="TokenHash">Digest of the secret the peer must present. Compared in constant time.</param>
public readonly record struct A2AOutboundCallback(
    string TaskId,
    string AgentUrl,
    string TokenHash,
    A2ASendingLedgerEntry Ledger);

/// <summary>
/// <see cref="IA2ASendingLedger"/> over the shared durable-operation ledger.
/// </summary>
/// <remarks>
/// Every method is best-effort: A2A must keep working when the Grimoire is unavailable, so a ledger
/// failure degrades to "no durable record" (the pre-#62 behavior) and is logged, never thrown at a peer.
/// </remarks>
internal sealed class A2ASendingLedger(
    ILongRunningOperationStore store,
    TimeProvider timeProvider,
    ILogger<A2ASendingLedger> logger,
    A2ASendingLeaseRenewer? leases = null) : IA2ASendingLedger
{

    internal const int CheckpointVersion = 1;

    /// <summary>
    /// A Sending has no whole-operation deadline (#55), so the lease marks ownership rather than sizing
    /// the work: <see cref="A2ASendingLeaseRenewer"/> renews it for as long as this process is holding
    /// the Sending, and it lapses when the process does.
    /// </summary>
    private static readonly TimeSpan LeaseDuration = A2ASendingLeaseRenewer.LeaseDuration;

    private static readonly string OwnerId = $"a2a-{Environment.ProcessId}";

    /// <summary>Rows fetched per lookup round-trip. A paging step, not a cap on in-flight Sendings.</summary>
    private const int PageSize = 200;

    public Task<A2ASendingLedgerEntry> RegisterInboundAsync(
        string taskId,
        Guid apprenticeId,
        CancellationToken cancellationToken = default) =>
        RegisterAsync(
            LongRunningOperationKinds.A2AInboundSending,
            new A2ASendingRecord
            {
                Direction = A2ASendingRecordDirection.Inbound,
                TaskId = taskId,
                ApprenticeId = apprenticeId,
            },
            $"Inbound A2A Sending serving task {taskId}.",
            cancellationToken);

    public Task<A2ASendingLedgerEntry> RegisterOutboundAsync(
        string remoteTaskId,
        string agentUrl,
        Guid? budgetReservationId = null,
        CancellationToken cancellationToken = default) =>
        RegisterAsync(
            LongRunningOperationKinds.A2AOutboundSending,
            new A2ASendingRecord
            {
                Direction = A2ASendingRecordDirection.Outbound,
                TaskId = remoteTaskId,
                AgentUrl = agentUrl,
            },
            $"Outbound A2A Sending awaiting remote task {remoteTaskId}.",
            budgetReservationId,
            cancellationToken);

    public async Task SettleOutboundAsync(
        A2ASendingLedgerEntry entry,
        A2ARemoteCost cost,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(cost);

        if (entry.IsRecorded)
        {

            try
            {

                LongRunningOperation? current = await store.GetAsync(entry.OperationId, cancellationToken).ConfigureAwait(false);

                if (current is not null && TryRead(current) is { } record)
                {

                    // The cost is written before the row closes, so a settled row always carries either a
                    // reported figure or an explicit "nobody said" — never an absence that reads as zero.
                    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                        record with
                        {
                            CostKnown = cost.IsKnown,
                            CostUsd = cost.CostUsd,
                            TotalTokens = cost.TotalTokens,
                        },
                        A2ASendingLedgerJsonContext.Default.A2ASendingRecord);

                    await store
                        .SaveCheckpointAsync(
                            entry.OperationId,
                            entry.OwnerId,
                            expectedCheckpointVersion: CheckpointVersion,
                            checkpointVersion: CheckpointVersion,
                            payload,
                            checkpointReference: null,
                            $"Outbound A2A Sending {record.TaskId} settled ({cost.Describe()}).",
                            timeProvider.GetUtcNow(),
                            cancellationToken)
                        .ConfigureAwait(false);

                }

            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {

                logger.LogWarning(
                    ex,
                    "A2A: could not record what settled Sending {OperationId} cost.",
                    entry.OperationId);

            }

        }

        await ReleaseAsync(entry, cancellationToken).ConfigureAwait(false);

    }

    public async Task ReleaseAsync(A2ASendingLedgerEntry entry, CancellationToken cancellationToken = default)
    {

        if (!entry.IsRecorded)
        {

            return;

        }

        // Settled: renewing a closed row's lease would keep it out of every later reconciliation pass.
        leases?.Forget(entry);

        try
        {

            LongRunningOperation? current = await store.GetAsync(entry.OperationId, cancellationToken).ConfigureAwait(false);

            if (current is null)
            {

                return;

            }

            bool closed = await store.TryTransitionAsync(
                    entry.OperationId,
                    current.Revision,
                    entry.OwnerId,
                    LongRunningOperationState.Completed,
                    timeProvider.GetUtcNow(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (closed)
            {

                return;

            }

            // The transition is owner-scoped, and a parked Sending outlives its own lease: nothing
            // heartbeats a record that is waiting on a peer, and reconciliation releases the lease when it
            // flags one. Retaking it here is what stops a legitimately settled Sending from being left
            // open forever, which reconciliation would then keep re-examining.
            DateTimeOffset now = timeProvider.GetUtcNow();

            LongRunningOperationLeaseResult lease = await store
                .TryAcquireLeaseAsync(entry.OperationId, OwnerId, now, now.Add(LeaseDuration), cancellationToken)
                .ConfigureAwait(false);

            if (!lease.Acquired)
            {

                return;

            }

            await store.TryTransitionAsync(
                    entry.OperationId,
                    lease.Operation.Revision,
                    OwnerId,
                    LongRunningOperationState.Completed,
                    timeProvider.GetUtcNow(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(ex, "A2A: could not close durable Sending record {OperationId}.", entry.OperationId);

        }

    }

    public async Task MarkParkedAsync(
        A2ASendingLedgerEntry entry,
        string? contextId,
        CancellationToken cancellationToken = default)
    {

        if (!entry.IsRecorded)
        {

            return;

        }

        // Waiting on a peer is not work: a parked Sending stops being renewed so reconciliation can flag
        // it 'a2a.inbound_parked_awaiting_answer', which is what keeps it answerable after a restart.
        leases?.Forget(entry);

        try
        {

            LongRunningOperation? current = await store.GetAsync(entry.OperationId, cancellationToken).ConfigureAwait(false);

            if (current is null || TryRead(current) is not { } record)
            {

                return;

            }

            A2ASendingRecord parked = record with { Parked = true, ContextId = contextId };

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                parked,
                A2ASendingLedgerJsonContext.Default.A2ASendingRecord);

            await store
                .SaveCheckpointAsync(
                    entry.OperationId,
                    entry.OwnerId,
                    expectedCheckpointVersion: CheckpointVersion,
                    checkpointVersion: CheckpointVersion,
                    payload,
                    checkpointReference: null,
                    $"Inbound A2A Sending {record.TaskId} parked awaiting the peer's answer.",
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);

            // Waiting is what a parked Sending actually is, and it keeps the row re-leasable so the
            // answer — this process's or a later one's — can close it out.
            LongRunningOperation? afterCheckpoint = await store
                .GetAsync(entry.OperationId, cancellationToken)
                .ConfigureAwait(false);

            if (afterCheckpoint is not null)
            {

                await store.TryTransitionAsync(
                        entry.OperationId,
                        afterCheckpoint.Revision,
                        entry.OwnerId,
                        LongRunningOperationState.Waiting,
                        timeProvider.GetUtcNow(),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(
                ex,
                "A2A: could not record that Sending {OperationId} is parked awaiting an answer.",
                entry.OperationId);

        }

    }

    public async Task<A2AParkedSending?> FindParkedInboundAsync(
        string taskId,
        bool takeLease = true,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(taskId))
        {

            return null;

        }

        try
        {

            LongRunningOperation? match = await FindOpenInboundAsync(
                    taskId,
                    static record => record.Parked,
                    cancellationToken)
                .ConfigureAwait(false);

            if (match is null || TryRead(match) is not { ApprenticeId: { } apprenticeId } record)
            {

                return null;

            }

            if (!takeLease)
            {

                return new A2AParkedSending(apprenticeId, record.ContextId, default);

            }

            // Take the lease when it is free so the resumed relay owns the record and can close it. When
            // it is not, the answer still reaches the Apprentice — only the bookkeeping stays elsewhere.
            DateTimeOffset now = timeProvider.GetUtcNow();

            LongRunningOperationLeaseResult lease = await store
                .TryAcquireLeaseAsync(match.Id, OwnerId, now, now.Add(LeaseDuration), cancellationToken)
                .ConfigureAwait(false);

            A2ASendingLedgerEntry resumed = lease.Acquired
                ? new A2ASendingLedgerEntry(match.Id, OwnerId)
                : default;

            // The answer restarts the relay, so the row is this process's work again and needs renewing
            // for as long as the resumed Apprentice runs.
            leases?.Track(resumed);

            return new A2AParkedSending(apprenticeId, record.ContextId, resumed);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(ex, "A2A: could not resolve a parked Sending record for task {TaskId}.", taskId);

            return null;

        }

    }

    public async Task<Guid?> FindInboundApprenticeAsync(string taskId, CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(taskId))
        {

            return null;

        }

        try
        {

            LongRunningOperation? match = await FindOpenInboundAsync(taskId, static _ => true, cancellationToken)
                .ConfigureAwait(false);

            return match is null ? null : TryRead(match)?.ApprenticeId;

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(ex, "A2A: could not resolve a durable Sending record for task {TaskId}.", taskId);

            return null;

        }

    }

    public async Task RecordOutboundCallbackAsync(
        A2ASendingLedgerEntry entry,
        string callbackConfigId,
        string callbackTokenHash,
        CancellationToken cancellationToken = default)
    {

        if (!entry.IsRecorded || string.IsNullOrWhiteSpace(callbackConfigId))
        {

            return;

        }

        try
        {

            LongRunningOperation? current = await store.GetAsync(entry.OperationId, cancellationToken).ConfigureAwait(false);

            if (current is null || TryRead(current) is not { } record)
            {

                return;

            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                record with { CallbackConfigId = callbackConfigId, CallbackTokenHash = callbackTokenHash },
                A2ASendingLedgerJsonContext.Default.A2ASendingRecord);

            await store
                .SaveCheckpointAsync(
                    entry.OperationId,
                    entry.OwnerId,
                    expectedCheckpointVersion: CheckpointVersion,
                    checkpointVersion: CheckpointVersion,
                    payload,
                    checkpointReference: null,
                    $"Outbound A2A Sending {record.TaskId} awaiting a peer callback.",
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(
                ex,
                "A2A: could not record the callback registration for Sending {OperationId}.",
                entry.OperationId);

        }

    }

    public async Task<A2AOutboundCallback?> FindOutboundCallbackAsync(
        string callbackConfigId,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(callbackConfigId))
        {

            return null;

        }

        try
        {

            for (int offset = 0; ; offset += PageSize)
            {

                IReadOnlyList<LongRunningOperation> page = await store
                    .ListAsync(
                        new LongRunningOperationQuery(
                            LongRunningOperationKinds.A2AOutboundSending,
                            Limit: PageSize,
                            Offset: offset),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (page.Count == 0)
                {

                    return null;

                }

                foreach (LongRunningOperation candidate in page)
                {

                    if (candidate.State is LongRunningOperationState.Completed
                        or LongRunningOperationState.Failed
                        or LongRunningOperationState.Abandoned)
                    {

                        continue;

                    }

                    if (TryRead(candidate) is
                        {
                            Direction: A2ASendingRecordDirection.Outbound,
                            CallbackConfigId: { Length: > 0 } configId,
                            CallbackTokenHash: { Length: > 0 } tokenHash,
                        } record
                        && string.Equals(configId, callbackConfigId, StringComparison.Ordinal))
                    {

                        return new A2AOutboundCallback(
                            record.TaskId,
                            record.AgentUrl ?? string.Empty,
                            tokenHash,
                            new A2ASendingLedgerEntry(candidate.Id, OwnerId));

                    }

                }

                if (page.Count < PageSize)
                {

                    return null;

                }

            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(
                ex,
                "A2A: could not resolve the Sending behind callback config {ConfigId}.",
                callbackConfigId);

            return null;

        }

    }

    /// <summary>
    /// Finds the still-open inbound record for <paramref name="taskId"/> that satisfies
    /// <paramref name="predicate"/>.
    /// </summary>
    /// <remarks>
    /// Paged to exhaustion rather than capped: a lookup that quietly stopped after N rows would be a
    /// hidden ceiling on how many Sendings an operator may have in flight, and the peer whose task fell
    /// past it would be told "nothing to cancel" while the work kept running.
    /// <para>
    /// <see cref="LongRunningOperationState.ReconciliationRequired"/> is deliberately <em>not</em> a
    /// closed state here: that is what reconciliation records for a Sending parked awaiting an answer,
    /// and such a record is still the live correspondence (issue #68).
    /// </para>
    /// </remarks>
    private async Task<LongRunningOperation?> FindOpenInboundAsync(
        string taskId,
        Func<A2ASendingRecord, bool> predicate,
        CancellationToken cancellationToken)
    {

        for (int offset = 0; ; offset += PageSize)
        {

            IReadOnlyList<LongRunningOperation> page = await store
                .ListAsync(
                    new LongRunningOperationQuery(
                        LongRunningOperationKinds.A2AInboundSending,
                        Limit: PageSize,
                        Offset: offset),
                    cancellationToken)
                .ConfigureAwait(false);

            if (page.Count == 0)
            {

                return null;

            }

            foreach (LongRunningOperation candidate in page)
            {

                if (candidate.State is LongRunningOperationState.Completed
                    or LongRunningOperationState.Failed
                    or LongRunningOperationState.Abandoned)
                {

                    continue;

                }

                if (TryRead(candidate) is { } record
                    && record.Direction == A2ASendingRecordDirection.Inbound
                    && string.Equals(record.TaskId, taskId, StringComparison.Ordinal)
                    && record.ApprenticeId is not null
                    && predicate(record))
                {

                    return candidate;

                }

            }

            if (page.Count < PageSize)
            {

                return null;

            }

        }

    }

    /// <summary>Reads a checkpointed record, treating any unreadable shape as "no record".</summary>
    internal static A2ASendingRecord? TryRead(LongRunningOperation operation) =>
        operation.CheckpointPayload is { Length: > 0 } payload
        && operation.CheckpointVersion == CheckpointVersion
            ? TryReadPayload(payload)
            : null;

    /// <inheritdoc cref="TryRead(LongRunningOperation)"/>
    internal static A2ASendingRecord? TryReadPayload(byte[] payload)
    {

        try
        {

            A2ASendingRecord? record = JsonSerializer.Deserialize(
                payload,
                A2ASendingLedgerJsonContext.Default.A2ASendingRecord);

            return record is { TaskId.Length: > 0 } ? record : null;

        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {

            return null;

        }

    }

    private async Task<A2ASendingLedgerEntry> RegisterAsync(
        string kind,
        A2ASendingRecord record,
        string publicSummary,
        CancellationToken cancellationToken) =>
        await RegisterAsync(kind, record, publicSummary, budgetReservationId: null, cancellationToken)
            .ConfigureAwait(false);

    private async Task<A2ASendingLedgerEntry> RegisterAsync(
        string kind,
        A2ASendingRecord record,
        string publicSummary,
        Guid? budgetReservationId,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(record.TaskId))
        {

            return default;

        }

        try
        {

            DateTimeOffset now = timeProvider.GetUtcNow();

            LongRunningOperation operation = await store
                .CreateAsync(
                    new LongRunningOperationCreateRequest(
                        kind,
                        LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                        publicSummary,
                        now,
                        BudgetReservationId: budgetReservationId),
                    cancellationToken)
                .ConfigureAwait(false);

            LongRunningOperationLeaseResult lease = await store
                .TryAcquireLeaseAsync(operation.Id, OwnerId, now, now.Add(LeaseDuration), cancellationToken)
                .ConfigureAwait(false);

            if (!lease.Acquired)
            {

                return default;

            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                record,
                A2ASendingLedgerJsonContext.Default.A2ASendingRecord);

            await store
                .SaveCheckpointAsync(
                    operation.Id,
                    OwnerId,
                    expectedCheckpointVersion: 0,
                    checkpointVersion: CheckpointVersion,
                    payload,
                    checkpointReference: null,
                    publicSummary,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);

            A2ASendingLedgerEntry entry = new(operation.Id, OwnerId);

            // Held from here until the Sending settles or parks. Without the renewal the 15-minute lease
            // lapses under any longer Sending and background reconciliation recovers the row out from
            // under the live call.
            leases?.Track(entry);

            return entry;

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            // A2A stays usable without the Grimoire: losing the durable record costs restart
            // reconciliation, not the Sending itself.
            logger.LogWarning(ex, "A2A: could not record a durable Sending for task {TaskId}.", record.TaskId);

            return default;

        }

    }

}

/// <summary>
/// Resolves an <see cref="IA2ASendingLedger"/> from a scope, for the singleton A2A services.
/// </summary>
internal static class A2ASendingLedgerScope
{

    internal static IA2ASendingLedger? Resolve(IServiceProvider services) =>
        services.GetService<IA2ASendingLedger>();

}

/// <summary>Source-generated contract so the durable Sending checkpoint stays Native AOT-safe.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(A2ASendingRecord))]
internal sealed partial class A2ASendingLedgerJsonContext : JsonSerializerContext;
