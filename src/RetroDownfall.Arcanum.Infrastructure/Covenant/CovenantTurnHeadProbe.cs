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

}
