CREATE TRIGGER IF NOT EXISTS campaign_contribution_artifacts_guard_update
BEFORE UPDATE ON campaign_contribution_artifacts
BEGIN
    SELECT RAISE(ABORT, 'campaign_contribution_artifacts revisions are immutable.');
END;
