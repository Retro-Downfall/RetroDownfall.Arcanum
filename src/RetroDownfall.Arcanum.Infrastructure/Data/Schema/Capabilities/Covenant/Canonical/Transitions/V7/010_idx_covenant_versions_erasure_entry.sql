CREATE INDEX IF NOT EXISTS idx_covenant_versions_erasure_entry
    ON covenant_versions(lower(replace(EntryId, '-', '')), LaneCode, LaneRevision);
