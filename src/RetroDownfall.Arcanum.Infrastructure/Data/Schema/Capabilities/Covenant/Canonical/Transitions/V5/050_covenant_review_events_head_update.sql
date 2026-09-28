CREATE TRIGGER IF NOT EXISTS covenant_review_events_head_update AFTER UPDATE OF CurrentVersionId ON covenant_heads WHEN old.CurrentVersionId <> new.CurrentVersionId BEGIN
    INSERT INTO covenant_review_events (DatasetGeneration, VersionId, EntryId, LaneCode, OperationCode, OriginCode, ScopeCode, CampaignId, SourceTurnId, SourceToolCallId)
    SELECT state.DatasetGeneration, version.VersionId, version.EntryId, version.LaneCode, version.OperationCode, version.OriginCode, entry.ScopeCode, entry.CampaignId, version.SourceTurnId, version.SourceToolCallId
    FROM covenant_versions AS version JOIN covenant_entries AS entry ON entry.EntryId = version.EntryId CROSS JOIN covenant_state AS state
    WHERE version.VersionId = new.CurrentVersionId AND state.StateKey = 1;
END;
