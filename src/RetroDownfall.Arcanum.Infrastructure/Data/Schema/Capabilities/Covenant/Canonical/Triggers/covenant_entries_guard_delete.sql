-- Entries leave only through owner cleanup or Covenant family maintenance, which remove the entry
-- together with its versions, heads, and provenance in one transaction. An unscoped delete would
-- strand that history. Both authorizations begin FALSE on every connection, so ordinary application
-- work reaches this guard and aborts.
CREATE TRIGGER IF NOT EXISTS covenant_entries_guard_delete
BEFORE DELETE ON covenant_entries
WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_entries delete requires an authorized cleanup scope.');
END;
