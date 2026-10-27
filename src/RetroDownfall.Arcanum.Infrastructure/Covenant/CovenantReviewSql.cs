namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>The exact parameterized Review statements executed by the service and explained by native tests.</summary>
/// <remarks>Fixed scope predicates make the existing partial indexes usable. No caller text enters SQL.</remarks>
internal static class CovenantReviewSql
{
    private const string EventSelect =
        """
        SELECT event.Sequence, event.EntryId, event.VersionId,
               event.OperationCode, event.OriginCode, event.ScopeCode, event.CampaignId,
               event.LaneCode, entry.NormalizedKey, version.CompiledContent,
               event.SourceTurnId, event.SourceToolCallId, version.LaneRevision,
               version.AuthoredHash, version.RenderedHash, version.CompiledByteCost,
               version.AttachmentProvenanceCount, version.AttachmentProvenanceDigest,
               entry.CreatedAtUtc, head.UpdatedAtUtc,
               CASE WHEN head.CurrentVersionId = event.VersionId THEN 1 ELSE 0 END,
               EXISTS (
                   SELECT 1 FROM covenant_review_decision_receipts reviewed
                   WHERE reviewed.ReviewEventSequence = event.Sequence)
        FROM covenant_review_events event
        JOIN covenant_versions version ON version.VersionId = event.VersionId
        JOIN covenant_entries entry ON entry.EntryId = event.EntryId
        LEFT JOIN covenant_heads head
          ON head.EntryId = event.EntryId AND head.LaneCode = event.LaneCode
        """;

    internal static string Marker(bool campaignScoped) => $"""
        SELECT DatasetGeneration, ReviewedThroughSequence, Revision
        FROM covenant_review_markers
        WHERE DatasetGeneration = $generation
          AND ScopeCode = $scope
          AND {CampaignPredicate(campaignScoped, false)}
          AND LaneCode = $lane;
        """;

    internal static string UpperFrontier(bool campaignScoped) => $"""
        SELECT COALESCE(MAX(Sequence), 0)
        FROM covenant_review_events
        WHERE DatasetGeneration = $generation
          AND ScopeCode = $scope
          AND {CampaignPredicate(campaignScoped, false)}
          AND LaneCode = $lane;
        """;

    internal static string Page(bool campaignScoped) => EventSelect + $"""

        WHERE event.DatasetGeneration = $generation
          AND event.ScopeCode = $scope
          AND {CampaignPredicate(campaignScoped, true)}
          AND event.LaneCode = $lane
          AND event.Sequence >= $lower
          AND event.Sequence <= $upper
          AND ($keyset IS NULL OR event.Sequence < $keyset)
          AND head.CurrentVersionId = event.VersionId
          AND NOT EXISTS (
              SELECT 1 FROM covenant_review_decision_receipts receipt
              WHERE receipt.ReviewEventSequence = event.Sequence)
        ORDER BY event.Sequence DESC
        LIMIT $limit;
        """;

    internal static string Event(bool campaignScoped) => EventSelect + $"""

        WHERE event.DatasetGeneration = $generation
          AND event.ScopeCode = $scope
          AND {CampaignPredicate(campaignScoped, true)}
          AND event.LaneCode = $lane
          AND event.Sequence = $sequence;
        """;

    internal static string CurationRevision(bool campaignScoped) => $"""
        SELECT COALESCE(MAX(CurrentRevision), 0)
        FROM covenant_curation_heads
        WHERE ScopeCode = $scope AND {CampaignPredicate(campaignScoped, false)}
          AND NormalizedKey = $key AND LaneCode = $lane AND KeyEpoch = $epoch;
        """;

    internal static string ReviewedThrough(bool campaignScoped) => $"""
        SELECT event.Sequence,
               CASE WHEN head.CurrentVersionId = event.VersionId THEN 1 ELSE 0 END,
               EXISTS (
                   SELECT 1 FROM covenant_review_decision_receipts receipt
                   WHERE receipt.ReviewEventSequence = event.Sequence)
        FROM covenant_review_events event
        LEFT JOIN covenant_heads head
          ON head.EntryId = event.EntryId AND head.LaneCode = event.LaneCode
        WHERE event.DatasetGeneration = $generation
          AND event.ScopeCode = $scope
          AND {CampaignPredicate(campaignScoped, true)}
          AND event.LaneCode = $lane
          AND event.Sequence > $current
        ORDER BY event.Sequence;
        """;

    internal static string AdvanceMarker(bool campaignScoped) => $"""
        UPDATE covenant_review_markers
        SET ReviewedThroughSequence = $through, Revision = Revision + 1
        WHERE DatasetGeneration = $generation
          AND ScopeCode = $scope
          AND {CampaignPredicate(campaignScoped, false)}
          AND LaneCode = $lane
          AND ReviewedThroughSequence = $expectedThrough
          AND Revision = $revision;
        """;

    private static string CampaignPredicate(bool campaignScoped, bool qualified) => (campaignScoped, qualified) switch
    {
        (true, true) => "event.CampaignId = $campaign",
        (true, false) => "CampaignId = $campaign",
        (false, true) => "event.CampaignId IS NULL",
        (false, false) => "CampaignId IS NULL",
    };
}
