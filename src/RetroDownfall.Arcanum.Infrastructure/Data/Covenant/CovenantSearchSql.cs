using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Every search command, in one place, so the query-plan suite explains the same strings the index
/// executes.
/// </summary>
/// <remarks>
/// The only interpolation is a choice between fixed literal predicates and a count of parameter
/// placeholders. Caller text reaches SQL exclusively as a bound parameter value produced by
/// <see cref="CovenantSearchQueryCompiler"/>.
/// </remarks>
internal static class CovenantSearchSql
{

    private const int CampaignOwnerKindCode = 1;

    /// <summary>
    /// BM25 weights: key, authored content, compiled content. Lower is better in SQLite's bm25.
    /// </summary>
    private const string Bm25 = "bm25(covenant_fts, 8.0, 3.0, 1.0)";

    internal static string Sources() => $"""
        SELECT st.DatasetGeneration,
               st.CanonicalSearchSequence,
               COALESCE((SELECT MAX(Sequence) FROM owner_deletion_events WHERE OwnerKindCode = {CampaignOwnerKindCode}), 0),
               st.AppliedDatasetGeneration,
               st.AppliedSearchSequence,
               st.AppliedCampaignDeletionSequence,
               st.AcceleratorEpoch
        FROM covenant_state st
        WHERE st.StateKey = 1;
        """;

    internal static string FtsPage(
        CovenantOperationScope? scope,
        bool laneFiltered,
        CovenantLifecycle lifecycle,
        bool continued) => $"""
        WITH matched AS (
            SELECT d.EntryId AS EntryId,
                   d.VersionId AS VersionId,
                   d.ScopeCode AS ScopeCode,
                   d.CampaignId AS CampaignId,
                   d.LaneCode AS LaneCode,
                   d.LifecycleCode AS LifecycleCode,
                   d.NormalizedKey AS NormalizedKey,
                   CASE
                       WHEN d.NormalizedKey = $exactKey THEN 1
                       WHEN d.NormalizedKey LIKE $prefixKey ESCAPE '\' THEN 2
                       ELSE 3
                   END AS MatchClass,
                   {Bm25} AS Score,
                   d.SearchRowId AS SearchRowId
            FROM covenant_fts
            JOIN covenant_search_documents d ON d.SearchRowId = covenant_fts.rowid
            WHERE covenant_fts MATCH $match
              AND {ScopeFilter(scope, "d")}
              AND {(laneFiltered ? "d.LaneCode = $lane" : "1 = 1")}
              AND {LifecycleFilter(lifecycle, "d.LifecycleCode")}
        )
        SELECT EntryId, VersionId, ScopeCode, CampaignId, LaneCode, LifecycleCode, NormalizedKey,
               MatchClass, Score, SearchRowId
        FROM matched
        WHERE {(continued
            ? "(MatchClass, Score, EntryId, VersionId) > ($afterClass, $afterScore, $afterEntry, $afterVersion)"
            : "1 = 1")}
        ORDER BY MatchClass, Score, EntryId, VersionId
        LIMIT $limit;
        """;

    /// <summary>
    /// The canonical fallback. <c>MATERIALIZED</c> is load-bearing: without the optimization barrier
    /// SQLite flattens the subquery and applies <c>LIKE</c> while scanning past the candidate cap,
    /// which is exactly the unbounded scan the cap exists to prevent.
    /// </summary>
    internal static string FallbackPage(
        CovenantOperationScope? scope,
        bool laneFiltered,
        CovenantLifecycle lifecycle,
        int termCount,
        bool continued)
    {

        string matches = string.Join(
            "\n              AND ",
            Enumerable.Range(0, termCount).Select(static index => $"""
                (NormalizedKey LIKE $like{index} ESCAPE '\'
                   OR COALESCE(AuthoredContent, '') LIKE $like{index} ESCAPE '\'
                   OR COALESCE(CompiledContent, '') LIKE $like{index} ESCAPE '\')
                """));

        return $"""
            WITH candidates AS MATERIALIZED (
                SELECT h.EntryId AS EntryId,
                       h.CurrentVersionId AS VersionId,
                       h.ScopeCode AS ScopeCode,
                       h.CampaignId AS CampaignId,
                       h.LaneCode AS LaneCode,
                       h.CurrentOperationCode AS LifecycleCode,
                       h.NormalizedKey AS NormalizedKey,
                       h.SearchRowId AS SearchRowId,
                       v.AuthoredContent AS AuthoredContent,
                       v.CompiledContent AS CompiledContent
                FROM covenant_heads h
                JOIN covenant_versions v ON v.VersionId = h.CurrentVersionId
                WHERE {ScopeFilter(scope, "h")}
                  AND {(laneFiltered ? "h.LaneCode = $lane" : "1 = 1")}
                  AND {LifecycleFilter(lifecycle, "h.CurrentOperationCode")}
                ORDER BY h.ScopeCode, COALESCE(h.CampaignId, ''), h.NormalizedKey, h.EntryId, h.LaneCode
                LIMIT {CovenantLimits.MaxFallbackCandidates}
            )
            SELECT EntryId, VersionId, ScopeCode, CampaignId, LaneCode, LifecycleCode, NormalizedKey,
                   CASE
                       WHEN NormalizedKey = $exactKey THEN 1
                       WHEN NormalizedKey LIKE $prefixKey ESCAPE '\' THEN 2
                       ELSE 3
                   END AS MatchClass,
                   0.0 AS Score,
                   SearchRowId
            FROM candidates
            WHERE {matches}
              AND {(continued
                ? "(MatchClass, EntryId, VersionId) > ($afterClass, $afterEntry, $afterVersion)"
                : "1 = 1")}
            ORDER BY MatchClass, EntryId, VersionId
            LIMIT $limit;
            """;

    }

    /// <summary>
    /// How many heads the fallback would have had to consider, so a truncated candidate set can be
    /// reported honestly rather than read as "no more results".
    /// </summary>
    internal static string FallbackCandidateCount(
        CovenantOperationScope? scope,
        bool laneFiltered,
        CovenantLifecycle lifecycle) => $"""
        SELECT COUNT(*)
        FROM covenant_heads h
        WHERE {ScopeFilter(scope, "h")}
          AND {(laneFiltered ? "h.LaneCode = $lane" : "1 = 1")}
          AND {LifecycleFilter(lifecycle, "h.CurrentOperationCode")};
        """;

    private static string ScopeFilter(CovenantOperationScope? scope, string alias) =>
        scope is null
            ? "1 = 1"
            : scope.Value.Kind == CovenantScope.Global
                ? $"{alias}.CampaignId IS NULL"
                : $"{alias}.CampaignId = $campaign";

    private static string LifecycleFilter(CovenantLifecycle lifecycle, string column) =>
        lifecycle switch
        {
            CovenantLifecycle.Set => $"{column} = 1",

            CovenantLifecycle.Retired => $"{column} = 2",

            _ => "1 = 1",
        };

}
