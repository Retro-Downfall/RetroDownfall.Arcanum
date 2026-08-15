-- The prior summary artifact is deleted only by the transaction that has already installed its
-- replacement and purged the derived indexes, or by a purge or retention scope that is removing the
-- Session's data outright. An unscoped delete would strand the current-state pointer and destroy the
-- evidence a still-live label refers to.
CREATE TRIGGER IF NOT EXISTS session_summary_artifacts_guard_delete
BEFORE DELETE ON session_summary_artifacts
WHEN arcanum_artifact_replacement_authorized() = 0
    AND arcanum_sensitivity_purge_authorized() = 0
    AND arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'session_summary_artifacts delete requires an authorized replacement, purge, or retention scope.');
END;
