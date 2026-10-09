CREATE TRIGGER IF NOT EXISTS campaign_rollup_artifacts_guard_delete
BEFORE DELETE ON campaign_rollup_artifacts
WHEN arcanum_artifact_replacement_authorized() = 0
    AND arcanum_sensitivity_purge_authorized() = 0
    AND arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'campaign_rollup_artifacts deletion requires an authorized purge or owner cleanup.');
END;
