ALTER TABLE campaign_rollup_artifacts ADD COLUMN ManifestId TEXT NULL REFERENCES campaign_rollup_manifests(ManifestId) DEFERRABLE INITIALLY DEFERRED;
CREATE INDEX IF NOT EXISTS idx_campaign_rollup_artifacts_manifest ON campaign_rollup_artifacts(ManifestId);
