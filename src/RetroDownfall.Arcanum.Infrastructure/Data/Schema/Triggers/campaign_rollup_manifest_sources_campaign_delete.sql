CREATE TRIGGER IF NOT EXISTS campaign_rollup_manifest_sources_campaign_delete
AFTER DELETE ON campaign_rollup_manifest_sources
BEGIN
    UPDATE campaign_rollup_state
    SET CurrentArtifactId = NULL, RefoldRequired = 1, SourceGeneration = SourceGeneration + 1, LastFoldedSessionId = NULL
    WHERE CampaignId = OLD.CampaignId AND CurrentArtifactId IN (
        SELECT artifact.ArtifactId FROM campaign_rollup_artifacts artifact
        JOIN campaign_rollup_manifests root ON root.ManifestId = artifact.ManifestId
        JOIN campaign_rollup_manifests consumed ON consumed.ManifestId = OLD.ManifestId
        WHERE root.LineageId = consumed.LineageId AND root.Ordinal >= consumed.Ordinal);
END;
