CREATE INDEX IF NOT EXISTS idx_covenant_review_events_erasure_version
    ON covenant_review_events(lower(replace(VersionId, '-', '')));
