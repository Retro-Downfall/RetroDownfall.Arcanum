CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_versions_head_candidate
    ON covenant_versions(VersionId, EntryId, LaneCode, LaneRevision, OperationCode);
