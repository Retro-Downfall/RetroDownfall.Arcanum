-- Provenance is deleted only alongside the version that owns it, under owner cleanup or Covenant
-- family maintenance. A standalone delete would leave the version's recorded provenance count and
-- digest describing rows that are no longer present. Both authorizations begin FALSE on every
-- connection, so ordinary application work reaches this guard and aborts.
CREATE TRIGGER IF NOT EXISTS covenant_version_attachment_provenance_guard_delete
BEFORE DELETE ON covenant_version_attachment_provenance
WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_version_attachment_provenance delete requires an authorized cleanup scope.');
END;
