CREATE TRIGGER IF NOT EXISTS campaign_rollup_artifacts_guard_update
BEFORE UPDATE ON campaign_rollup_artifacts
BEGIN
    SELECT RAISE(ABORT, 'campaign_rollup_artifacts revisions are immutable.');
END;
