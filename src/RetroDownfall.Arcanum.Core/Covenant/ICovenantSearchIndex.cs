using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The sole free-text inspection port over current Covenant heads.
/// </summary>
/// <remarks>
/// One signature for both execution modes. FTS5 being missing, stale, corrupt, locked, or
/// version-mismatched changes the <see cref="CovenantSearchPage.ExecutionMode"/> of a successful
/// page, never which method the caller has to call: a second query API would make degradation
/// something every caller had to handle rather than something the port absorbs.
/// </remarks>
public interface ICovenantSearchIndex
{

    ValueTask<Result<CovenantSearchPage>> SearchAsync(
        CovenantSearchQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

}
