CREATE TRIGGER IF NOT EXISTS campaign_rollup_sources_manifest_guard
BEFORE INSERT ON campaign_rollup_sources
WHEN EXISTS (SELECT 1 FROM campaign_rollup_artifacts WHERE ArtifactId = NEW.RollupArtifactId AND ManifestId IS NOT NULL)
BEGIN
    SELECT RAISE(ABORT, 'A shared Campaign root cannot also carry legacy source rows.');
END;
