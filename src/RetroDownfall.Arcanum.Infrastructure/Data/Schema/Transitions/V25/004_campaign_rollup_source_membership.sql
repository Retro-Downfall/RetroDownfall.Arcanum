-- Logical membership only; retention counts the shared physical rows separately.
CREATE VIEW IF NOT EXISTS campaign_rollup_source_membership AS
SELECT RollupArtifactId, SessionId, CampaignId, RollupRevision, ContributionArtifactId,
    ContributionRevision, ContentDigest, SensitivityDigest, SummarizedThroughSequence
FROM campaign_rollup_sources
UNION ALL
SELECT artifact.ArtifactId, source.SessionId, source.CampaignId, artifact.Revision,
    source.ContributionArtifactId, source.ContributionRevision, source.ContentDigest,
    source.SensitivityDigest, source.SummarizedThroughSequence
FROM campaign_rollup_artifacts artifact
JOIN campaign_rollup_manifests root ON root.ManifestId = artifact.ManifestId
JOIN campaign_rollup_manifests segment ON segment.LineageId = root.LineageId AND segment.Ordinal <= root.Ordinal
JOIN campaign_rollup_manifest_sources source ON source.ManifestId = segment.ManifestId;
