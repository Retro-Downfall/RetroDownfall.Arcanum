CREATE INDEX IF NOT EXISTS idx_covenant_versions_erasure_version
    ON covenant_versions(lower(replace(VersionId, '-', '')));
