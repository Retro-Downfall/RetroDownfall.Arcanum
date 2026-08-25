using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one bounded head read a live turn may make for a key its plan never carried.
/// </summary>
/// <remarks>
/// A staging handler learns three things and no more: whether a lane head exists, at which revision,
/// and whether it is a tombstone. The turn's own lease and Campaign are captured here rather than
/// passed per call, so a tool cannot probe a scope its turn does not cover — the narrowing is
/// structural rather than a rule the handler has to observe.
///
/// <para>Holds the turn's lease without owning it. The lease belongs to the turn context and is
/// released when that context is disposed; a probe that disposed it would end the turn's admission
/// halfway through the turn.</para>
/// </remarks>
internal sealed class CovenantTurnHeadProbe(
    ICovenantStore store,
    CanonicalCampaignContext campaign,
    ICovenantSnapshotReadLease readLease) : ICovenantTurnHeadProbe
{

    public ValueTask<Result<CovenantLaneHeadProbe>> ProbeAsync(
        CovenantLane lane,
        string normalizedKey,
        CancellationToken cancellationToken) =>
        store.ProbeLaneHeadAsync(campaign, lane, normalizedKey, readLease, cancellationToken);

    /// <summary>
    /// Measures the turn's own Campaign Section, under the turn's own lease.
    /// </summary>
    /// <remarks>
    /// The scope is derived from the captured Campaign rather than passed in, for the same reason the
    /// head probe's is: a tool call must not be able to measure a Section its turn does not cover. A
    /// Global-only turn has no Proposed Section to measure and no way to reach this, because the
    /// capability that carries it refuses to exist without a Campaign binding.
    /// </remarks>
    /// <summary>
    /// Measures the scope this turn writes into, under the turn's own lease.
    /// </summary>
    /// <remarks>
    /// The scope is derived from the captured Campaign for the same reason the Section probe's is: a
    /// tool call must not be able to measure a scope its turn does not cover.
    /// </remarks>
    public ValueTask<Result<CovenantQuotaSnapshot>> ProbeScopeAsync(
        ImmutableArray<string> excludedKeys,
        CancellationToken cancellationToken) =>
        store.ReadQuotaSnapshotAsync(
            campaign.IsCampaignBound
                ? CovenantOperationScope.ForCampaign(campaign.CampaignId!.Value)
                : CovenantOperationScope.Global,
            excludedKeys,
            readLease,
            cancellationToken);

    public ValueTask<Result<CovenantSectionOccupancy>> ProbeSectionAsync(
        CovenantLane lane,
        ImmutableArray<string> excludedKeys,
        CancellationToken cancellationToken) =>
        store.ReadSectionOccupancyAsync(
            new CovenantSectionOccupancyQuery(
                campaign.IsCampaignBound
                    ? CovenantOperationScope.ForCampaign(campaign.CampaignId!.Value)
                    : CovenantOperationScope.Global,
                lane,
                excludedKeys),
            readLease,
            cancellationToken);

}
