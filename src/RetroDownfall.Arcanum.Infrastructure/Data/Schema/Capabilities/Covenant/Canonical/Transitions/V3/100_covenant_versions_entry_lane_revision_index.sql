CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_versions_entry_lane_revision
    ON covenant_versions(EntryId, LaneCode, LaneRevision);
