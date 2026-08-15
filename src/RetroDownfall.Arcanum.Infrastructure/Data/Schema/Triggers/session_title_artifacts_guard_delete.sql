-- A clean operator-authored title may remove the prior tainted artifact, but only from the same
-- authorized replacement transaction that overwrote the title and its projections. Purge and
-- retention scopes may also remove it. Nothing else may, because deleting the artifact alone would
-- leave the current-state pointer and any live label describing bytes that are gone.
CREATE TRIGGER IF NOT EXISTS session_title_artifacts_guard_delete
BEFORE DELETE ON session_title_artifacts
WHEN arcanum_artifact_replacement_authorized() = 0
    AND arcanum_sensitivity_purge_authorized() = 0
    AND arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'session_title_artifacts delete requires an authorized replacement, purge, or retention scope.');
END;
