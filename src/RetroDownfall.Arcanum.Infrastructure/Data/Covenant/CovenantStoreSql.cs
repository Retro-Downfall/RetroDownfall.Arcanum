using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Every canonical read command, in one place.
/// </summary>
/// <remarks>
/// The store executes exactly these strings and the query-plan suite explains exactly these strings.
/// Keeping the SQL in one shared builder is what makes the plan assertions evidence rather than
/// decoration: a plan test that reconstructed similar SQL of its own would keep passing after the
/// store's real query lost an index.
///
/// <para>Nothing here interpolates a caller value. The only interpolation is a choice between fixed
/// literal predicates, because a partial index is usable only when the query's Campaign predicate
/// matches the index's own <c>WHERE</c> clause; a single <c>OR</c> spanning both would force a scan.
/// </para>
/// </remarks>
internal static class CovenantStoreSql
{

    internal const int CampaignOwnerKindCode = 1;

    /// <summary>
    /// The projected head-and-version columns every canonical head read shares, in one fixed order.
    /// </summary>
    private const string HeadProjection = """
        h.SearchRowId AS SearchRowId,
        h.EntryId AS EntryId,
        h.CurrentVersionId AS VersionId,
        h.NormalizedKey AS NormalizedKey,
        h.ScopeCode AS ScopeCode,
        h.CampaignId AS CampaignId,
        h.LaneCode AS LaneCode,
        h.CurrentOperationCode AS OperationCode,
        h.CurrentLaneRevision AS LaneRevision,
        h.CompiledByteCost AS HeadByteCost,
        h.OriginCode AS HeadOriginCode,
        h.UpdatedAtUtc AS UpdatedAtUtc,
        v.OriginCode AS VersionOriginCode,
        v.PredecessorVersionId AS PredecessorVersionId,
        v.CompilerPolicyVersion AS CompilerPolicyVersion,
        v.RendererPolicyVersion AS RendererPolicyVersion,
        v.AuthoredHash AS AuthoredHash,
        v.RenderedHash AS RenderedHash,
        v.AttachmentProvenanceCount AS ProvenanceCount,
        v.AttachmentProvenanceDigest AS ProvenanceDigest,
        v.CompiledContent AS CompiledContent
        """;

    /// <summary>
    /// The one hot-path command: Global Confirmed plus the canonical Campaign's live heads, with the
    /// dataset facts every snapshot binds, in a single round trip.
    /// </summary>
    internal static string TurnSnapshot() => $"""
        SELECT st.DatasetGeneration, st.CanonicalSearchSequence, st.KeyReclamationEpoch, candidates.*
        FROM covenant_state st
        LEFT JOIN (
            SELECT {HeadProjection}
            FROM covenant_heads h
            JOIN covenant_versions v ON v.VersionId = h.CurrentVersionId
            WHERE h.CampaignId IS NULL AND h.LaneCode = 1 AND h.CurrentOperationCode = 1
            UNION ALL
            SELECT {HeadProjection}
            FROM covenant_heads h
            JOIN covenant_versions v ON v.VersionId = h.CurrentVersionId
            WHERE h.CampaignId = $campaign AND h.CurrentOperationCode = 1
        ) AS candidates
        WHERE st.StateKey = 1
        LIMIT {CovenantLimits.ActiveSnapshotProbeRows};
        """;

    /// <summary>
    /// The Global keys one Campaign has masked, for the turn that Campaign is about to take.
    /// </summary>
    /// <remarks>
    /// A separate command from the turn snapshot rather than another arm of its union, and issued only
    /// for a Campaign-bound turn: a mask is Campaign-scoped by construction, so a Global-only turn can
    /// hold none and pays nothing. It seeks <c>idx_covenant_curation_heads_campaign_masks</c>.
    ///
    /// <para>The key epoch is joined rather than ignored. A mask recorded against a key that was later
    /// retired and reclaimed describes a key this installation no longer has, and applying it to the
    /// one that re-created the name would suppress content the operator never masked.</para>
    /// </remarks>
    internal static string CampaignMasks() => """
        SELECT h.NormalizedKey
        FROM covenant_curation_heads h
        WHERE h.CampaignId = $campaign
          AND h.IsMasked = 1
          AND h.LaneCode = 1
          AND h.KeyEpoch = COALESCE(
              (SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = h.NormalizedKey), 0);
        """;

    internal static string LaneHeadProbe(bool campaignScoped) => $"""
        SELECT epochs.KeyEpoch, h.EntryId, h.CurrentVersionId, h.CurrentLaneRevision,
               h.CurrentOperationCode, h.OriginCode, h.CompiledByteCost
        FROM (
            SELECT COALESCE(MAX(KeyEpoch), 0) AS KeyEpoch
            FROM covenant_key_epochs
            WHERE NormalizedKey = $key
        ) AS epochs
        LEFT JOIN covenant_heads h
            ON {(campaignScoped ? "h.CampaignId = $campaign" : "h.CampaignId IS NULL")}
               AND h.NormalizedKey = $key AND h.LaneCode = $lane
        LIMIT 1;
        """;

    /// <summary>
    /// The three Section measures for one scoped lane, ignoring an explicit key list.
    /// </summary>
    /// <remarks>
    /// Deliberately the same shape and the same three columns the quota guard reads inside its write
    /// transaction. Two statements that measured a Section differently would let a staging preflight
    /// accept what the commit then refuses, which is the whole failure this query exists to prevent.
    ///
    /// <para>It names no key, so neither <c>active</c> partial index applies -- both lead with
    /// <c>NormalizedKey</c> -- and the planner seeks <c>idx_covenant_heads_campaign_cleanup</c>
    /// instead. That is the intended plan rather than a missed one: a Section is measured whole, and
    /// the scope's own entry ceiling is what bounds the rows the seek walks.</para>
    /// </remarks>
    /// <summary>
    /// Every scope-wide count one capacity decision reads, as a single row.
    /// </summary>
    /// <remarks>
    /// Shared because two callers now ask this question: the publication authority, inside the
    /// transaction that will commit, and the staging preflight, on a read lease before a tool call
    /// accepts anything. Two callers reading different rows is precisely the shape that costs an
    /// operator their reply — the preflight admits what the commit then refuses — so the statement
    /// lives here rather than being written twice.
    /// </remarks>
    /// <summary>
    /// The Sessions whose turn receipts have outgrown their live tail, most burdened first.
    /// </summary>
    /// <remarks>
    /// Discovery for the compaction sweep, which folds one Session per call and cannot find its own
    /// work. Bounded by <c>$take</c> so a pass costs the same on an installation with one backlog and
    /// on one with thousands, and ordered by depth so the Sessions closest to their ceiling are the
    /// ones a bounded pass spends itself on.
    /// </remarks>
    internal static string TurnReceiptBacklog() => """
        SELECT SessionId
        FROM covenant_turn_receipts
        GROUP BY SessionId
        HAVING COUNT(*) > $ceiling
        ORDER BY COUNT(*) DESC
        LIMIT $take;
        """;

    internal static string QuotaSnapshot(bool campaignScoped, int excludedKeyCount)
    {

        // The keys this batch is about to rewrite, removed from the two counters that measure active
        // heads. A write to a key that already has one replaces that head rather than adding a head,
        // so counting the row it is about to supersede charges the write for a row it does not add,
        // and at the ceiling that refuses an operator the right to change their mind about something
        // they already said. The ceiling itself is untouched: a key with no active head is excluded
        // from nothing, so a genuinely new entry is still measured against the full count.
        string notTouched = excludedKeyCount == 0
            ? "1 = 1"
            : $"NormalizedKey NOT IN ({string.Join(", ", Enumerable.Range(0, excludedKeyCount).Select(static index => $"$xkey{index}"))})";

        string scopePredicate = campaignScoped ? "CampaignId = $campaign" : "CampaignId IS NULL";

        // A Global batch has no Campaign of its own, so the pair it has to fit inside is the one the
        // widest Campaign would form with it; a Campaign batch is measured against its own pair.
        string campaignSideOfTurnLoad = campaignScoped
            ? $"(SELECT COUNT(*) FROM covenant_heads WHERE CampaignId = $campaign AND CurrentOperationCode = 1 AND {notTouched})"
            : """
                COALESCE((SELECT MAX(HeadCount) FROM (
                    SELECT COUNT(*) AS HeadCount FROM covenant_heads
                    WHERE CampaignId IS NOT NULL AND CurrentOperationCode = 1
                    GROUP BY CampaignId)), 0)
                """;

        return $"""
            SELECT
                (SELECT COUNT(*) FROM covenant_heads WHERE {scopePredicate} AND CurrentOperationCode = 1 AND {notTouched}),
                (SELECT COUNT(*) FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate}),
                (SELECT COUNT(*) FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate} AND v.OperationCode = 1),
                (SELECT COALESCE(SUM(v.CompiledByteCost), 0) FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate}),
                (SELECT COUNT(*) FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate} AND v.OriginCode IN (2, 3)),
                (SELECT COALESCE(SUM(v.CompiledByteCost), 0) FROM covenant_versions v
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate} AND v.OriginCode IN (2, 3)),
                (SELECT COUNT(*) FROM covenant_mutation_receipts WHERE {scopePredicate}),
                (SELECT COUNT(*) FROM covenant_version_attachment_provenance p
                    JOIN covenant_versions v ON v.VersionId = p.VersionId
                    JOIN covenant_entries e ON e.EntryId = v.EntryId
                    WHERE e.{scopePredicate}),
                (SELECT COUNT(*) FROM covenant_search_outbox),
                (SELECT COUNT(*) FROM covenant_heads WHERE CampaignId IS NULL AND CurrentOperationCode = 1
                    AND {(campaignScoped ? "1 = 1" : notTouched)})
                    + {campaignSideOfTurnLoad};
            """;

    }

    internal static string SectionOccupancy(bool campaignScoped, int excludedKeyCount) => $"""
        SELECT COUNT(*),
               COALESCE(SUM(h.CompiledByteCost), 0),
               COALESCE(MAX(v.RequiredFenceLength), 0)
        FROM covenant_heads h
        JOIN covenant_versions v ON v.VersionId = h.CurrentVersionId
        WHERE {(campaignScoped ? "h.CampaignId = $campaign" : "h.CampaignId IS NULL")}
          AND h.LaneCode = $lane
          AND h.CurrentOperationCode = 1
          AND {(excludedKeyCount == 0
              ? "1 = 1"
              : $"h.NormalizedKey NOT IN ({string.Join(", ", Enumerable.Range(0, excludedKeyCount).Select(static index => $"$key{index}"))})")};
        """;

    internal static string ListPage(
        CovenantOperationScope? scope,
        bool laneFiltered,
        CovenantLifecycle lifecycle,
        bool continued)
    {

        string scopeFilter = scope is null
            ? "1 = 1"
            : scope.Value.Kind == CovenantScope.Global
                ? "h.CampaignId IS NULL"
                : "h.CampaignId = $campaign";

        string laneFilter = laneFiltered ? "h.LaneCode = $lane" : "1 = 1";

        string lifecycleFilter = lifecycle switch
        {
            CovenantLifecycle.Set => "h.CurrentOperationCode = 1",

            CovenantLifecycle.Retired => "h.CurrentOperationCode = 2",

            _ => "1 = 1",
        };

        string keysetFilter = continued
            ? """
              (h.ScopeCode, COALESCE(h.CampaignId, ''), h.NormalizedKey, h.EntryId, h.LaneCode)
                  > ($afterScope, $afterCampaign, $afterKey, $afterEntry, $afterLane)
              """
            : "1 = 1";

        return $"""
            SELECT st.DatasetGeneration, st.CanonicalSearchSequence, deletions.Sequence, page.*
            FROM covenant_state st
            LEFT JOIN (
                SELECT COALESCE(MAX(Sequence), 0) AS Sequence
                FROM owner_deletion_events
                WHERE OwnerKindCode = {CampaignOwnerKindCode}
            ) AS deletions
            LEFT JOIN (
                SELECT {HeadProjection}, e.AuthoredKey AS AuthoredKey, e.CreatedAtUtc AS CreatedAtUtc
                FROM covenant_heads h
                JOIN covenant_entries e ON e.EntryId = h.EntryId
                JOIN covenant_versions v ON v.VersionId = h.CurrentVersionId
                WHERE {scopeFilter} AND {laneFilter} AND {lifecycleFilter} AND {keysetFilter}
                ORDER BY h.ScopeCode, COALESCE(h.CampaignId, ''), h.NormalizedKey, h.EntryId, h.LaneCode
                LIMIT $limit
            ) AS page
            WHERE st.StateKey = 1
            ORDER BY page.ScopeCode, COALESCE(page.CampaignId, ''), page.NormalizedKey, page.EntryId, page.LaneCode;
            """;

    }

    internal static string Detail(bool campaignScoped) => $"""
        SELECT st.DatasetGeneration, st.CanonicalSearchSequence, epochs.KeyEpoch, lanes.*
        FROM covenant_state st
        LEFT JOIN (
            SELECT COALESCE(MAX(KeyEpoch), 0) AS KeyEpoch
            FROM covenant_key_epochs
            WHERE NormalizedKey = $key
        ) AS epochs
        LEFT JOIN (
            SELECT {HeadProjection}, e.AuthoredKey AS AuthoredKey, e.CreatedAtUtc AS CreatedAtUtc
            FROM covenant_heads h
            JOIN covenant_entries e ON e.EntryId = h.EntryId
            JOIN covenant_versions v ON v.VersionId = h.CurrentVersionId
            WHERE {(campaignScoped ? "h.CampaignId = $campaign" : "h.CampaignId IS NULL")}
              AND h.NormalizedKey = $key
        ) AS lanes
        WHERE st.StateKey = 1;
        """;

    internal static string VersionPage(bool continued) => $"""
        SELECT st.DatasetGeneration, st.CanonicalSearchSequence, page.*
        FROM covenant_state st
        LEFT JOIN (
            SELECT v.VersionId, v.EntryId, v.LaneCode, v.LaneRevision, v.OperationCode, v.OriginCode,
                   v.AuthoredHash, v.RenderedHash, v.CompiledByteCost, v.CompilerPolicyVersion,
                   v.RendererPolicyVersion, v.PredecessorVersionId, v.MutationId,
                   v.AttachmentProvenanceCount, v.AttachmentProvenanceDigest, v.CreatedAtUtc
            FROM covenant_versions v
            WHERE v.EntryId = $entry AND v.LaneCode = $lane
              AND {(continued ? "v.LaneRevision < $afterRevision" : "1 = 1")}
            ORDER BY v.LaneRevision DESC
            LIMIT $limit
        ) AS page
        WHERE st.StateKey = 1
        ORDER BY page.LaneRevision DESC;
        """;

    /// <summary>
    /// One command, one join, no N+1. Sixty-four leaves is the hard bound; the extra row is how an
    /// over-full version is detected rather than silently truncated.
    /// </summary>
    internal static string SourcePage() => $"""
        SELECT v.AttachmentProvenanceCount, v.AttachmentProvenanceDigest,
               p.Ordinal, p.AttachmentId, p.AttachmentVersionIdentity, p.LogicalKey, p.ContentHash,
               p.SourceRangeKindCode, p.SourceStart, p.SourceEnd, p.SourceTurnId, p.MaterializationReference
        FROM covenant_versions v
        LEFT JOIN covenant_version_attachment_provenance p ON p.VersionId = v.VersionId
        WHERE v.VersionId = $version
        ORDER BY p.Ordinal
        LIMIT {CovenantLimits.MaxVersionSources + 1};
        """;

    /// <summary>
    /// The narrow indexed scan behind Global effect streaming: every current Campaign ID beside the
    /// heads that share one normalized key, with no compiled text or provenance in the projection.
    /// </summary>
    /// <remarks>
    /// This is the only Covenant statement that joins its own identity text to the EF-owned
    /// <c>"Campaigns"</c> table, and the two sides disagree on case: EF binds a <see cref="Guid"/>
    /// through the provider, which stores an uppercase <c>D</c>-format literal, while
    /// <c>covenant_heads.CampaignId</c> is written by the mutation kernel as a lowercase one. Neither
    /// column is <c>COLLATE NOCASE</c>, so an unnormalized <c>=</c> matched nothing for any identity
    /// containing a hex letter and every Campaign silently reported no head.
    ///
    /// <para>The normalization sits on the outer <c>"Campaigns"</c> row rather than on
    /// <c>h.CampaignId</c> so <c>idx_covenant_heads_campaign_active</c> still drives the per-Campaign
    /// head lookup that keeps a Global scan from going quadratic. The scoped predicate stays a bare
    /// column comparison, and its parameter is bound as a <see cref="Guid"/> instead, so the
    /// <c>"Campaigns"</c> primary key index still serves it.</para>
    /// </remarks>
    internal static string DependentHeadScan(bool allCampaigns) => $"""
        SELECT c."Id",
               MAX(CASE WHEN h.LaneCode = 1 THEN h.CurrentLaneRevision ELSE 0 END) AS ConfirmedRevision,
               MAX(CASE WHEN h.LaneCode = 2 THEN h.CurrentLaneRevision ELSE 0 END) AS ProposedRevision
        FROM "Campaigns" c
        LEFT JOIN covenant_heads h
            ON h.CampaignId = lower(c."Id") AND h.NormalizedKey = $key AND h.CurrentOperationCode = 1
        {(allCampaigns ? string.Empty : "WHERE c.\"Id\" = $campaign")}
        GROUP BY c."Id"
        ORDER BY c."Id";
        """;

    internal static string EffectFacts() => """
        SELECT st.DatasetGeneration, st.CanonicalSearchSequence, st.KeyReclamationEpoch,
               COALESCE((SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = $key), 0),
               COALESCE((SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1), 0)
        FROM covenant_state st
        WHERE st.StateKey = 1;
        """;

    internal static string LocalHeadFactsSql(bool campaignScoped) => $"""
        SELECT h.LaneCode, h.CurrentOperationCode
        FROM covenant_heads h
        WHERE {(campaignScoped ? "h.CampaignId = $campaign" : "h.CampaignId IS NULL")}
          AND h.NormalizedKey = $key;
        """;

    /// <summary>
    /// One curation subject's current state, the two head facts a broader-scope sentence is read off,
    /// and every epoch a preflight token binds, in one round trip.
    /// </summary>
    /// <remarks>
    /// The two head predicates ask about live heads only. A retired head applies to no turn, so a mask
    /// over a key whose Global head is a tombstone suppresses nothing, and telling an operator otherwise
    /// would be describing an effect they will not get.
    ///
    /// <para>The subject's key epoch is read once into a common table expression and joined from there.
    /// Repeating the sub-select per column would let two of them disagree if the epoch advanced between
    /// them, and the disagreement would land on the curation row a commit then failed to find.</para>
    /// </remarks>
    internal static string CurationEffectFacts(bool campaignScoped)
    {

        string campaignPredicate = campaignScoped ? "$campaign" : "NULL";

        string scopedConfirmed = campaignScoped
            ? "EXISTS(SELECT 1 FROM covenant_heads c WHERE c.CampaignId = $campaign AND c.NormalizedKey = $key"
                + " AND c.LaneCode = 1 AND c.CurrentOperationCode = 1)"
            : "0";

        return $"""
            WITH epoch(Value) AS (
                SELECT COALESCE((SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = $key), 0)
            )
            SELECT st.DatasetGeneration,
                   st.KeyReclamationEpoch,
                   epoch.Value,
                   EXISTS(SELECT 1 FROM covenant_heads g WHERE g.CampaignId IS NULL AND g.NormalizedKey = $key
                          AND g.LaneCode = 1 AND g.CurrentOperationCode = 1),
                   {scopedConfirmed},
                   COALESCE(ch.IsPinned, 0),
                   COALESCE(ch.IsMasked, 0),
                   COALESCE(ch.CurrentRevision, 0)
            FROM covenant_state st
            CROSS JOIN epoch
            LEFT JOIN covenant_curation_heads ch
                ON ch.CampaignId IS {campaignPredicate}
                   AND ch.NormalizedKey = $key
                   AND ch.LaneCode = $lane
                   AND ch.KeyEpoch = epoch.Value
            WHERE st.StateKey = 1;
            """;

    }

}
