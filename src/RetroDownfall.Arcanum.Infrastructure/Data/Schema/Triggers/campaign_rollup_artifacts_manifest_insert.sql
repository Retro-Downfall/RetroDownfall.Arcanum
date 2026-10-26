CREATE TRIGGER IF NOT EXISTS campaign_rollup_artifacts_manifest_insert
BEFORE INSERT ON campaign_rollup_artifacts
WHEN NEW.ManifestId IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM campaign_rollup_manifests manifest WHERE manifest.ManifestId = NEW.ManifestId
        AND manifest.CampaignId = NEW.CampaignId AND manifest.SourceCount = NEW.SourceCount
        AND manifest.SourceManifestDigest = NEW.SourceManifestDigest)
BEGIN
    SELECT RAISE(ABORT, 'A Campaign publication must bind its exact immutable manifest.');
END;
