CREATE TRIGGER IF NOT EXISTS campaign_rollup_manifest_sources_guard_delete
BEFORE DELETE ON campaign_rollup_manifest_sources
WHEN arcanum_artifact_replacement_authorized() = 0
    AND arcanum_sensitivity_purge_authorized() = 0
    AND arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'Campaign provenance deletion requires authorized cleanup.');
END;
