CREATE INDEX IF NOT EXISTS idx_covenant_provenance_erasure_version
    ON covenant_version_attachment_provenance(lower(replace(VersionId, '-', '')));
