namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

internal sealed partial class LexiconService
{
    private static string RecallMatchColumns(bool curation, bool cleanOnly, string alias) =>
        cleanOnly ? CleanRecallColumns(curation, alias) : $"""
            {alias}.Id, {alias}.Name, {alias}.Type, {alias}.FactsJson, {alias}.UpdatedAt, {alias}.ScopeCampaignId,
            {(curation ? alias + ".RetiredAtUtc" : "NULL")} AS RetiredAtUtc,
            {(curation ? alias + ".PinnedAtUtc" : "NULL")} AS PinnedAtUtc,
            {(curation ? alias + ".CurationGeneration" : "1")} AS CurationGeneration
            """;

    // A matched oversized row must refuse the page, not disappear from it. NULL sentinels stop
    // ReadEntry before content hydration; all content columns share one code-owned allocation bound.
    private static string CleanRecallColumns(bool curation, string alias)
    {
        string bounded = $"length(CAST({alias}.Name AS BLOB)) + length(CAST({alias}.Type AS BLOB)) + length(CAST({alias}.FactsJson AS BLOB)) <= 65536";

        return $"""
            {alias}.Id,
            CASE WHEN {bounded} THEN {alias}.Name ELSE NULL END,
            CASE WHEN {bounded} THEN {alias}.Type ELSE NULL END,
            CASE WHEN {bounded} THEN {alias}.FactsJson ELSE NULL END,
            {alias}.UpdatedAt, {alias}.ScopeCampaignId,
            {(curation ? alias + ".RetiredAtUtc" : "NULL")} AS RetiredAtUtc,
            {(curation ? alias + ".PinnedAtUtc" : "NULL")} AS PinnedAtUtc,
            {(curation ? alias + ".CurationGeneration" : "1")} AS CurationGeneration
            """;
    }

    // Applied inside each exact/FTS/fallback selection before LIMIT. A malformed spelling of a
    // protected identity still excludes it, and a missing current head cannot establish clean state.
    private static string CleanRecallPredicate(string alias) => $"""
         AND NOT EXISTS (
            SELECT 1 FROM artifact_sensitivity label
            WHERE label.ArtifactKindCode = 7
                AND lower(replace(label.ArtifactId, '-', '')) = lower(replace({alias}.Id, '-', '')))
         AND NOT EXISTS (
            SELECT 1 FROM owner_deletion_events deleted
            WHERE deleted.OwnerKindCode = 1 AND {alias}.ScopeCampaignId <> ''
                AND lower(replace(deleted.OwnerId, '-', '')) = lower(replace({alias}.ScopeCampaignId, '-', '')))
         AND NOT EXISTS (
            SELECT 1 FROM annal_claims claim
            LEFT JOIN annal_heads head ON head.ClaimId = claim.ClaimId AND head.SubjectStoreCode = 2
            LEFT JOIN annal_versions version ON version.VersionId = head.CurrentVersionId AND version.ClaimId = claim.ClaimId
            WHERE claim.SubjectStoreCode = 2
                AND lower(replace(claim.SubjectId, '-', '')) = lower(replace({alias}.Id, '-', ''))
                AND (version.VersionId IS NULL OR version.SensitivityCode <> 0 OR version.OperationCode = 3
                    OR version.Revision <> head.CurrentRevision OR version.OperationCode <> head.CurrentOperationCode
                    OR version.ScopeKindCode <> CASE WHEN {alias}.ScopeCampaignId = '' THEN 1 ELSE 2 END
                    OR lower(replace(COALESCE(version.CampaignId, ''), '-', '')) <> lower(replace({alias}.ScopeCampaignId, '-', ''))
                    OR EXISTS (SELECT 1 FROM owner_deletion_events deleted
                        WHERE deleted.OwnerKindCode = 2
                            AND lower(replace(deleted.OwnerId, '-', '')) = lower(replace(version.SourceSessionId, '-', '')))))
         AND NOT EXISTS (
            SELECT 1 FROM lexicon_fact_attachment_provenance source
            JOIN owner_deletion_events deleted ON deleted.OwnerKindCode = 2
                AND lower(replace(deleted.OwnerId, '-', '')) = lower(replace(source.SessionId, '-', ''))
            WHERE source.EntryId = {alias}.Id)
        """;
}
