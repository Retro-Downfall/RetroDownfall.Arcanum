-- Provenance is deleted only alongside the version that owns it, under owner cleanup, Covenant family
-- maintenance, or the erasure of that version's entry. A standalone delete would leave the version's
-- recorded provenance count and digest describing rows that are no longer present. All three
-- authorizations begin FALSE on every connection, so ordinary application work reaches this guard and
-- aborts.
CREATE TRIGGER IF NOT EXISTS covenant_version_attachment_provenance_guard_delete
BEFORE DELETE ON covenant_version_attachment_provenance
WHEN arcanum_owner_cleanup_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
    AND arcanum_covenant_entry_erasure_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_version_attachment_provenance delete requires an authorized cleanup scope.');
END;
