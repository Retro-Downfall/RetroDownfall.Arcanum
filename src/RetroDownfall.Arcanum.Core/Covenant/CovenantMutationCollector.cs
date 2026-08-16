using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The linearizable lifecycle of one turn's mutation collector.
/// </summary>
/// <remarks>
/// <see cref="Discarded"/> is terminal and irreversible. A collector that could be reopened after a
/// cancellation would let a tool call that completed after the turn ended publish into the next one.
/// </remarks>
public enum CovenantCollectorState : byte
{
    Open = 1,
    Sealing = 2,
    Sealed = 3,
    Discarded = 4,
}

/// <summary>
/// The compact, content-free receipt a staged mutation exposes to public tool events.
/// </summary>
/// <remarks>
/// No key and no content. Covenant MCP arguments carry profile content, so the public stream gets an
/// opaque target identity and an outcome, and the operator reads the authored value back through the
/// authenticated Covenant API instead (§10.13).
/// </remarks>
public sealed record CovenantStagedMutationReceipt(
    Guid MutationId,
    CovenantDigest OpaqueTargetDigest,
    CovenantScope ScopeKind,
    CovenantLane Lane,
    long ExpectedLaneRevision,
    CovenantDigest? RenderedHash);

/// <summary>
/// Accepts eligible agent mutation intents for exactly one logical top-level turn.
/// </summary>
public interface ICovenantMutationCollector
{

    Guid LogicalTurnId { get; }

    CovenantDigest BasePlanDigest { get; }

    CovenantCollectorState State { get; }

    Guid CurrentBranchId { get; }

    int StagedCount { get; }

    /// <summary>Takes the in-flight use lease a staging attempt runs under.</summary>
    Result<IDisposable> TryAcquireUse();

    /// <summary>Opens a replacement branch, carrying the shared-prefix intents into it.</summary>
    Result OpenBranch(Guid branchId, ulong sharedPrefixOrdinal);

    /// <summary>Marks a branch terminal and discards every intent that belonged only to it.</summary>
    Result AbandonBranch(Guid branchId);

    Result<CovenantStagedMutationReceipt> Stage(
        CovenantMutationIntent intent,
        CovenantAdmissionReceipt producingAdmission,
        CovenantDigest toolInputDigest);

    /// <summary>Freezes one immutable batch after filtering to the committed branch's lineage.</summary>
    Result<ImmutableArray<CovenantMutationIntent>> Seal(Guid committedBranchId, ulong finalBranchOrdinal);

    void Discard();

}

/// <summary>
/// The in-memory per-turn collector.
/// </summary>
/// <remarks>
/// Staging never writes canonical state. An MCP handler that wrote directly would publish a profile
/// change for a turn whose answer was never persisted, which is precisely the outcome the
/// transaction barrier exists to prevent: the agent would have "remembered" something the operator
/// never received an answer about (§10.13).
///
/// <para>Every method is synchronized. Concurrent MCP requests inside one turn are ordinary, and
/// replay lookup, target uniqueness, and capacity all have to be decided against the same state or
/// two calls can each observe the last free slot.</para>
/// </remarks>
public sealed class CovenantMutationCollector : ICovenantMutationCollector
{

    private readonly Lock _gate = new();

    private readonly List<Entry> _entries = [];

    private readonly Dictionary<ReplayKey, ReplayRecord> _replay = [];

    private readonly HashSet<Guid> _abandonedBranches = [];

    private int _outstandingUses;

    public CovenantMutationCollector(
        Guid logicalTurnId,
        CovenantDigest basePlanDigest,
        Guid initialBranchId)
    {

        LogicalTurnId = CovenantValidation.RequireNonEmpty(logicalTurnId, nameof(logicalTurnId));

        BasePlanDigest = CovenantValidation.RequireDigest(basePlanDigest, nameof(basePlanDigest));

        CurrentBranchId = CovenantValidation.RequireNonEmpty(initialBranchId, nameof(initialBranchId));

    }

    public Guid LogicalTurnId { get; }

    public CovenantDigest BasePlanDigest { get; }

    public CovenantCollectorState State { get; private set; } = CovenantCollectorState.Open;

    public Guid CurrentBranchId { get; private set; }

    public int StagedCount
    {

        get
        {

            lock (_gate)
            {
                return _entries.Count;
            }

        }

    }

    public Result<IDisposable> TryAcquireUse()
    {

        lock (_gate)
        {

            if (State != CovenantCollectorState.Open)
            {
                return Result<IDisposable>.Failure(ClosedError());
            }

            _outstandingUses++;

            return Result<IDisposable>.Success(new UseLease(this));

        }

    }

    public Result OpenBranch(Guid branchId, ulong sharedPrefixOrdinal)
    {

        CovenantValidation.RequireNonEmpty(branchId, nameof(branchId));

        lock (_gate)
        {

            if (State != CovenantCollectorState.Open)
            {
                return Result.Failure(ClosedError());
            }

            if (_abandonedBranches.Contains(branchId))
            {
                return Result.Failure(new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "An abandoned Covenant branch can never be resumed."));
            }

            // Intents at or before the shared prefix belong to the ancestry both branches share, so
            // they move; anything after it belonged only to the branch being replaced.
            for (int index = _entries.Count - 1; index >= 0; index--)
            {

                if (_entries[index].BranchId == CurrentBranchId
                    && _entries[index].BranchOrdinal <= sharedPrefixOrdinal)
                {
                    _entries[index] = _entries[index] with { BranchId = branchId };
                }

            }

            _ = _abandonedBranches.Add(CurrentBranchId);

            RemoveAbandoned();

            CurrentBranchId = branchId;

            return Result.Success();

        }

    }

    public Result AbandonBranch(Guid branchId)
    {

        CovenantValidation.RequireNonEmpty(branchId, nameof(branchId));

        lock (_gate)
        {

            if (State is CovenantCollectorState.Discarded)
            {
                return Result.Failure(ClosedError());
            }

            _ = _abandonedBranches.Add(branchId);

            RemoveAbandoned();

            return Result.Success();

        }

    }

    public Result<CovenantStagedMutationReceipt> Stage(
        CovenantMutationIntent intent,
        CovenantAdmissionReceipt producingAdmission,
        CovenantDigest toolInputDigest)
    {

        ArgumentNullException.ThrowIfNull(intent);

        ArgumentNullException.ThrowIfNull(producingAdmission);

        CovenantValidation.RequireDigest(toolInputDigest, nameof(toolInputDigest));

        lock (_gate)
        {

            if (State != CovenantCollectorState.Open)
            {
                return Result<CovenantStagedMutationReceipt>.Failure(ClosedError());
            }

            if (intent.SourceTurnId != LogicalTurnId)
            {
                return Result<CovenantStagedMutationReceipt>.Failure(new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "A staged Covenant mutation must name its own logical turn."));
            }

            if (intent.BasePlanDigest != BasePlanDigest
                || intent.AdmissionReceiptDigest != producingAdmission.Digest)
            {
                return Result<CovenantStagedMutationReceipt>.Failure(new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "A staged Covenant mutation must bind the turn plan and the admission that produced it."));
            }

            if (string.IsNullOrEmpty(intent.SourceToolCallId))
            {
                return Result<CovenantStagedMutationReceipt>.Failure(new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "A staged Covenant mutation must name the tool call that produced it."));
            }

            // Replay is resolved before uniqueness and capacity. The reverse order would turn an
            // exact retry of an already-staged tool call into a duplicate-target rejection.
            ReplayKey replayKey = new(producingAdmission.Digest, intent.SourceToolCallId);

            if (_replay.TryGetValue(replayKey, out ReplayRecord? existing))
            {

                return existing.ToolInputDigest == toolInputDigest
                    ? Result<CovenantStagedMutationReceipt>.Success(existing.Receipt)
                    : Result<CovenantStagedMutationReceipt>.Failure(new Error(
                        ErrorCodes.Security.IdempotencyConflict,
                        "This Covenant tool call identity was already staged with different input."));

            }

            if (_entries.Any(entry =>
                entry.BranchId == CurrentBranchId
                && entry.Intent.Target.Lane == intent.Target.Lane
                && entry.Intent.Target.Scope == intent.Target.Scope
                && string.Equals(
                    entry.Intent.Target.NormalizedKey.Value,
                    intent.Target.NormalizedKey.Value,
                    StringComparison.Ordinal)))
            {
                return Result<CovenantStagedMutationReceipt>.Failure(new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    "This Covenant target is already staged for this turn."));
            }

            if (_entries.Count(entry => entry.BranchId == CurrentBranchId)
                >= CovenantLimits.MaxStagedMutationsPerTurn)
            {
                return Result<CovenantStagedMutationReceipt>.Failure(new Error(
                    ErrorCodes.Covenant.CapacityExceeded,
                    "This turn has already staged the maximum number of Covenant mutations."));
            }

            ulong ordinal = producingAdmission.BranchOrdinal;

            CovenantStagedMutationReceipt receipt = new(
                intent.MutationId,
                intent.Target.IdentityDigest,
                intent.Target.Scope.Kind,
                intent.Target.Lane,
                intent.ExpectedLaneRevision,
                intent.Artifact?.RenderedHash);

            _entries.Add(new Entry(intent, CurrentBranchId, ordinal, receipt));

            _replay[replayKey] = new ReplayRecord(toolInputDigest, receipt);

            return Result<CovenantStagedMutationReceipt>.Success(receipt);

        }

    }

    public Result<ImmutableArray<CovenantMutationIntent>> Seal(
        Guid committedBranchId,
        ulong finalBranchOrdinal)
    {

        CovenantValidation.RequireNonEmpty(committedBranchId, nameof(committedBranchId));

        lock (_gate)
        {

            if (State == CovenantCollectorState.Sealed)
            {
                return Result<ImmutableArray<CovenantMutationIntent>>.Success(Frozen(
                    committedBranchId,
                    finalBranchOrdinal));
            }

            if (State != CovenantCollectorState.Open)
            {
                return Result<ImmutableArray<CovenantMutationIntent>>.Failure(ClosedError());
            }

            if (_outstandingUses > 0)
            {
                return Result<ImmutableArray<CovenantMutationIntent>>.Failure(new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "A Covenant mutation is still in flight for this turn."));
            }

            State = CovenantCollectorState.Sealing;

            ImmutableArray<CovenantMutationIntent> batch = Frozen(committedBranchId, finalBranchOrdinal);

            HashSet<string> targets = [];

            foreach (CovenantMutationIntent intent in batch)
            {

                if (!targets.Add(
                    $"{(int)intent.Target.Scope.Kind}{intent.Target.Scope.CampaignId}{(int)intent.Target.Lane}{intent.Target.NormalizedKey.Value}"))
                {

                    State = CovenantCollectorState.Discarded;

                    return Result<ImmutableArray<CovenantMutationIntent>>.Failure(new Error(
                        ErrorCodes.Covenant.RevisionConflict,
                        "The sealed Covenant batch contains two mutations for one target."));

                }

            }

            State = CovenantCollectorState.Sealed;

            return Result<ImmutableArray<CovenantMutationIntent>>.Success(batch);

        }

    }

    public void Discard()
    {

        lock (_gate)
        {

            State = CovenantCollectorState.Discarded;

            _entries.Clear();

            _replay.Clear();

        }

    }

    private ImmutableArray<CovenantMutationIntent> Frozen(
        Guid committedBranchId,
        ulong finalBranchOrdinal) =>
        [
            .. _entries
                .Where(entry => entry.BranchId == committedBranchId
                    && entry.BranchOrdinal <= finalBranchOrdinal)
                .Select(static entry => entry.Intent)
        ];

    private void RemoveAbandoned() =>
        _ = _entries.RemoveAll(entry => _abandonedBranches.Contains(entry.BranchId));

    private Error ClosedError() =>
        new(
            ErrorCodes.Covenant.LifecycleConflict,
            State switch
            {
                CovenantCollectorState.Sealing => "This turn's Covenant collector is sealing.",
                CovenantCollectorState.Sealed => "This turn's Covenant collector is already sealed.",
                _ => "This turn's Covenant collector was discarded.",
            });

    private void ReleaseUse()
    {

        lock (_gate)
        {

            if (_outstandingUses > 0)
            {
                _outstandingUses--;
            }

        }

    }

    private sealed record Entry(
        CovenantMutationIntent Intent,
        Guid BranchId,
        ulong BranchOrdinal,
        CovenantStagedMutationReceipt Receipt);

    private readonly record struct ReplayKey(CovenantDigest AdmissionDigest, string ToolCallId);

    private sealed record ReplayRecord(
        CovenantDigest ToolInputDigest,
        CovenantStagedMutationReceipt Receipt);

    private sealed class UseLease(CovenantMutationCollector owner) : IDisposable
    {

        private bool _released;

        public void Dispose()
        {

            if (_released)
            {
                return;
            }

            _released = true;

            owner.ReleaseUse();

        }

    }

}
