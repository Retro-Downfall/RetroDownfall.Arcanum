-- Deleting a label is deleting the only record that an artifact is tainted, so it is allowed only
-- from a scope that is also removing or replacing the artifact itself: artifact replacement,
-- sensitivity retention purge, Session retention, owner cleanup, or Covenant family maintenance.
-- Every one of these begins FALSE on each connection, so ordinary application work reaches this
-- guard and aborts rather than quietly leaving a tainted artifact with no label.
CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_guard_delete
BEFORE DELETE ON artifact_sensitivity
WHEN arcanum_artifact_replacement_authorized() = 0
    AND arcanum_sensitivity_purge_authorized() = 0
    AND arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'artifact_sensitivity delete requires an authorized replacement, purge, retention, or maintenance scope.');
END;
