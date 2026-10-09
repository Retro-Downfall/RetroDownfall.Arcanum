CREATE TRIGGER IF NOT EXISTS campaign_fork_frontiers_guard_delete
BEFORE DELETE ON campaign_fork_frontiers
WHEN arcanum_artifact_replacement_authorized() = 0
    AND arcanum_sensitivity_purge_authorized() = 0
    AND arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'campaign_fork_frontiers deletion requires an authorized purge or owner cleanup.');
END;
