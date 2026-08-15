using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The bounded canonical read port.
/// </summary>
/// <remarks>
/// Every method takes a caller-owned snapshot-read lease and validates its coverage and generations
/// before opening a read transaction. It never acquires a lease of its own: a store that could
/// escalate its own coverage would make the gate's drain guarantee unprovable, because a reader
/// could appear inside an operation that had already finished draining.
///
/// <para>Reads only. Mutation goes through the transactional kernel, which owns its own transaction
/// and its own write authorization.</para>
/// </remarks>
public interface ICovenantStore
{

    /// <summary>
    /// The single bounded turn load: Global Confirmed plus the canonical Campaign's Confirmed and
    /// Proposed <c>Set</c> heads, in one command inside one read snapshot.
    /// </summary>
    ValueTask<Result<CovenantTurnSnapshot>> ReadTurnSnapshotAsync(
        CanonicalCampaignContext campaign,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantLaneHeadProbe>> ProbeLaneHeadAsync(
        CanonicalCampaignContext campaign,
        CovenantLane lane,
        string normalizedKey,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantListPage>> ReadListPageAsync(
        CovenantListQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantDetail>> ReadDetailAsync(
        CovenantDetailQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantVersionPage>> ReadVersionPageAsync(
        CovenantVersionQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantSourcePage>> ReadSourcePageAsync(
        CovenantSourceQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<CovenantMutationEffectSnapshot>> ReadMutationEffectSnapshotAsync(
        CovenantMutationEffectQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

}
