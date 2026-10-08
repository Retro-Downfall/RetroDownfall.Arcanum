using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// A store that forwards every read to a real one, so a test overrides exactly the read it wants to
/// observe or break and leaves the rest answering from storage.
/// </summary>
internal class DelegatingCovenantStore(ICovenantStore inner) : ICovenantStore
{
    public virtual ValueTask<Result<CovenantTurnSnapshot>> ReadTurnSnapshotAsync(
        CanonicalCampaignContext campaign,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadTurnSnapshotAsync(campaign, readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantLaneHeadProbe>> ProbeLaneHeadAsync(
        CanonicalCampaignContext campaign,
        CovenantLane lane,
        string normalizedKey,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ProbeLaneHeadAsync(campaign, lane, normalizedKey, readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantListPage>> ReadListPageAsync(
        CovenantListQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadListPageAsync(query, readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantDetail>> ReadDetailAsync(
        CovenantDetailQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadDetailAsync(query, readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantVersionPage>> ReadVersionPageAsync(
        CovenantVersionQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadVersionPageAsync(query, readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantSourcePage>> ReadSourcePageAsync(
        CovenantSourceQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadSourcePageAsync(query, readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantSectionOccupancy>> ReadSectionOccupancyAsync(
        CovenantSectionOccupancyQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadSectionOccupancyAsync(query, readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantQuotaSnapshot>> ReadQuotaSnapshotAsync(
        CovenantOperationScope scope,
        ImmutableArray<string> excludedKeys,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadQuotaSnapshotAsync(scope, excludedKeys, readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantScopeCensus>> ReadScopeCensusAsync(
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadScopeCensusAsync(readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantMutationEffectSnapshot>> ReadMutationEffectSnapshotAsync(
        CovenantMutationEffectQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadMutationEffectSnapshotAsync(query, readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantCurationEffectSnapshot>> ReadCurationEffectSnapshotAsync(
        CovenantCurationEffectQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadCurationEffectSnapshotAsync(query, readLease, cancellationToken);

    public virtual ValueTask<Result<CovenantRetirementTarget>> ReadRetirementTargetAsync(
        CanonicalCampaignContext campaign,
        CovenantLane lane,
        string normalizedKey,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken) =>
        inner.ReadRetirementTargetAsync(campaign, lane, normalizedKey, readLease, cancellationToken);
}
