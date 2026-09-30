-- Entries leave only through owner cleanup, Covenant family maintenance, or the erasure of that one
-- entry, each of which removes the entry together with its versions, heads, and provenance in one
-- transaction. An unscoped delete would strand that history. All three authorizations begin FALSE on
-- every connection, so ordinary application work reaches this guard and aborts.
CREATE TRIGGER IF NOT EXISTS covenant_entries_guard_delete
BEFORE DELETE ON covenant_entries
WHEN arcanum_owner_cleanup_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
    AND arcanum_covenant_entry_erasure_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_entries delete requires an authorized cleanup scope.');
END;
