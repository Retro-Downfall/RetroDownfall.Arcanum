using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// Who a finalization-capacity slot was allocated for.
/// </summary>
/// <remarks>
/// A public claim reserves its slot before any provider work starts and consumes it later, so a turn
/// can never reach the point of needing a guard and discover the ceiling is full. Internal, imported,
/// and forked guards have no waiting period at all: their guarded row is written in the same
/// transaction, so they allocate an already-consumed slot.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<AssistantFinalizationCapacityOrigin>))]
public enum AssistantFinalizationCapacityOrigin : byte
{

    PublicClaim = 1,

    Internal = 2,

    Imported = 3,

    Forked = 4,

}

/// <summary>
/// Where one capacity reservation stands. Both non-reserved states are terminal.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<AssistantFinalizationCapacityState>))]
public enum AssistantFinalizationCapacityState : byte
{

    Reserved = 1,

    Consumed = 2,

    Released = 3,

}

/// <summary>
/// The exact identity a consume or release names.
/// </summary>
/// <remarks>
/// All three fields, not just the reservation ID. A reservation that could be consumed by naming its
/// ID alone could be consumed by a Session that does not own it, and the counters it moves belong to
/// that owner.
/// </remarks>
public readonly record struct AssistantFinalizationCapacityIdentity
{

    public AssistantFinalizationCapacityIdentity(Guid reservationId, Guid sessionId, Guid assistantEntryId)
    {

        ReservationId = CovenantValidation.RequireNonEmpty(reservationId, nameof(reservationId));

        SessionId = CovenantValidation.RequireNonEmpty(sessionId, nameof(sessionId));

        AssistantEntryId = CovenantValidation.RequireNonEmpty(assistantEntryId, nameof(assistantEntryId));

    }

    public Guid ReservationId { get; }

    public Guid SessionId { get; }

    public Guid AssistantEntryId { get; }

}

/// <summary>
/// A public claim reserving one claim slot and one future finalization guard together.
/// </summary>
public sealed record SessionTurnCapacityReservationRequest
{

    public SessionTurnCapacityReservationRequest(
        Guid reservationId,
        Guid sessionId,
        Guid assistantEntryId,
        Guid claimId)
    {

        Identity = new AssistantFinalizationCapacityIdentity(reservationId, sessionId, assistantEntryId);

        ClaimId = CovenantValidation.RequireNonEmpty(claimId, nameof(claimId));

    }

    public AssistantFinalizationCapacityIdentity Identity { get; }

    public Guid ClaimId { get; }

}

/// <summary>
/// An internal, imported, or forked guard allocating an already-consumed slot.
/// </summary>
/// <remarks>
/// A public claim is deliberately not accepted here. It has a claim identity to bind and a waiting
/// period to survive, and letting it take the direct path would allocate a guard slot with no claim
/// that could ever release it.
/// </remarks>
public sealed record DirectFinalizationCapacityRequest
{

    public DirectFinalizationCapacityRequest(
        Guid reservationId,
        Guid sessionId,
        Guid assistantEntryId,
        AssistantFinalizationCapacityOrigin origin)
    {

        Identity = new AssistantFinalizationCapacityIdentity(reservationId, sessionId, assistantEntryId);

        Origin = origin is AssistantFinalizationCapacityOrigin.Internal
            or AssistantFinalizationCapacityOrigin.Imported
            or AssistantFinalizationCapacityOrigin.Forked
            ? origin
            : throw new ArgumentOutOfRangeException(
                nameof(origin),
                "A public claim must reserve its finalization capacity rather than allocating it directly.");

    }

    public AssistantFinalizationCapacityIdentity Identity { get; }

    public AssistantFinalizationCapacityOrigin Origin { get; }

}

/// <summary>
/// Whole-Session retention returning lifetime capacity installation-wide.
/// </summary>
/// <remarks>
/// The expected counts are a compare-and-swap, not a hint. Retention decrements the installation
/// totals by the exact values it locked from the Session row it is about to delete; decrementing by
/// anything else would silently rewrite the installation ceiling.
/// </remarks>
public sealed record SessionTurnCapacityReleaseRequest
{

    public SessionTurnCapacityReleaseRequest(
        Guid sessionId,
        long expectedClaimCount,
        long expectedReservedCount,
        long expectedConsumedCount)
    {

        SessionId = CovenantValidation.RequireNonEmpty(sessionId, nameof(sessionId));

        ExpectedClaimCount = Require(expectedClaimCount, nameof(expectedClaimCount));

        ExpectedReservedCount = Require(expectedReservedCount, nameof(expectedReservedCount));

        ExpectedConsumedCount = Require(expectedConsumedCount, nameof(expectedConsumedCount));

    }

    public Guid SessionId { get; }

    public long ExpectedClaimCount { get; }

    public long ExpectedReservedCount { get; }

    public long ExpectedConsumedCount { get; }

    private static long Require(long value, string parameterName) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(parameterName);

}

/// <summary>
/// The durable state of one capacity reservation after a transition.
/// </summary>
public sealed record AssistantFinalizationCapacityReservation(
    AssistantFinalizationCapacityIdentity Identity,
    AssistantFinalizationCapacityOrigin Origin,
    Guid? ClaimId,
    AssistantFinalizationCapacityState State,
    bool Replayed);

/// <summary>
/// The canonical counters a prospective Covenant mutation batch is checked against.
/// </summary>
/// <remarks>
/// Read once per batch inside the write transaction. Reading them per intent would let two intents
/// in the same batch each see room that only one of them can actually take.
///
/// <para><c>ActiveEntriesInWidestTurnLoad</c> is the only counter that spans two scopes: it is the
/// Global active head count plus the active head count of whichever Campaign a turn could bind it
/// to. A turn loads exactly that pair, so a batch that keeps every single-scope counter inside its
/// bound can still push the pair past what a snapshot may carry.</para>
/// </remarks>
public sealed record CovenantQuotaSnapshot(
    long ActiveEntriesInScope,
    long VersionsInScope,
    long SetVersionsInScope,
    long CanonicalBytesInScope,
    long AgentVersionsInCampaign,
    long AgentBytesInCampaign,
    long MutationReceiptsInScope,
    long ProvenanceRowsInCampaign,
    long PendingOutboxRows,
    long ActiveEntriesInWidestTurnLoad)
{

    /// <summary>An installation holding nothing, for callers describing an untouched scope.</summary>
    public static CovenantQuotaSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

}

/// <summary>
/// What one prospective batch would add to those counters.
/// </summary>
public sealed record CovenantQuotaDemand(
    long NewEntries,
    long NewVersions,
    long NewSetVersions,
    long NewCanonicalBytes,
    long NewAgentVersions,
    long NewAgentBytes,
    long NewMutationReceipts,
    long NewProvenanceRows,
    long NewOutboxRows,
    ImmutableArray<CovenantSectionDemand> Sections)
{

    /// <summary>
    /// The upper bound on what one batch of intents would add.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative: an intent that turns out to be a no-op or an update simply consumes
    /// less than it reserved, which is the safe direction for a ceiling. A revision is therefore
    /// charged a whole new head even though it replaces one, and that is why the staging preflight
    /// must build its demand here rather than reason about the batch itself — a preflight that was
    /// less conservative than the authority would admit a batch the commit refuses, and the commit
    /// carries the operator's reply.
    /// </remarks>
    public static CovenantQuotaDemand ForBatch(IEnumerable<CovenantMutationIntent> intents)
    {

        ArgumentNullException.ThrowIfNull(intents);

        long entries = 0;

        long versions = 0;

        long setVersions = 0;

        long canonicalBytes = 0;

        long agentVersions = 0;

        long agentBytes = 0;

        long receipts = 0;

        long provenanceRows = 0;

        foreach (CovenantMutationIntent intent in intents)
        {

            entries = checked(entries + 1);

            versions = checked(versions + 1);

            receipts = checked(receipts + 1);

            provenanceRows = checked(provenanceRows + intent.Provenance.Length);

            long bytes = intent.Artifact?.CompiledByteCost ?? 0;

            canonicalBytes = checked(canonicalBytes + bytes);

            if (intent.Operation == CovenantOperation.Set)
            {

                setVersions = checked(setVersions + 1);

            }

            if (intent.Origin is CovenantOrigin.AgentProposed or CovenantOrigin.AgentApproved)
            {

                agentVersions = checked(agentVersions + 1);

                agentBytes = checked(agentBytes + bytes);

            }

        }

        return new CovenantQuotaDemand(
            entries,
            versions,
            setVersions,
            canonicalBytes,
            agentVersions,
            agentBytes,
            receipts,
            provenanceRows,
            versions,
            CovenantSectionCapacity.Demands(intents));

    }

}

/// <summary>
/// What one prospective batch would leave in one rendered Section of one scope.
/// </summary>
/// <remarks>
/// Separate from the scope-wide counters because a Section is a different unit with a much smaller
/// ceiling: a scope holds hundreds of entries and megabytes, while the Section those entries render
/// into holds a few thousand bytes. Every scope-wide quota can be comfortably satisfied by a batch
/// that still renders a Section past its bound, and a Section past its bound is not a failed write
/// but a Covenant that stops reaching the model at all.
///
/// <para><see cref="TouchedKeys"/> is what makes editing possible. A <c>Set</c> replaces an entry's
/// contribution rather than adding to it, so the keys this batch writes have their present cost
/// subtracted before the batch's own cost is added; without that, re-setting an existing entry would
/// be charged twice and an ordinary edit would be refused.</para>
/// </remarks>
public sealed record CovenantSectionDemand(
    CovenantLane Lane,
    ImmutableArray<string> TouchedKeys,
    long NewEntries,
    long NewFragmentBytes,
    int RequiredFenceLength);

/// <summary>
/// What one rendered Section already holds that a prospective batch will not replace.
/// </summary>
/// <remarks>
/// The three measures a Section is sized from, and the only three. Two readers of the same Section
/// have to return the same shape or the preflight that admits a proposal and the authority that
/// publishes it can disagree about whether it fits — and the operator pays that disagreement with the
/// whole turn, because the batch and the answer commit together.
/// </remarks>
public readonly record struct CovenantSectionOccupancy(
    long Entries,
    long FragmentBytes,
    int LongestFenceLength)
{

    /// <summary>The occupancy of a Section that holds nothing this batch will not replace.</summary>
    public static CovenantSectionOccupancy Empty { get; }

}

/// <summary>
/// One bounded Section measurement, ignoring the keys a prospective batch is about to rewrite.
/// </summary>
public sealed record CovenantSectionOccupancyQuery(
    CovenantOperationScope Scope,
    CovenantLane Lane,
    ImmutableArray<string> ExcludedKeys);
