CREATE TRIGGER IF NOT EXISTS campaign_contribution_state_guard_delete
BEFORE DELETE ON campaign_contribution_state
WHEN arcanum_artifact_replacement_authorized() = 0
    AND arcanum_sensitivity_purge_authorized() = 0
    AND arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'campaign_contribution_state deletion requires an authorized purge or owner cleanup.');
END;
