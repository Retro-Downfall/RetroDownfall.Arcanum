-- Compaction removes events every installed capability has already acknowledged. That decision needs
-- the whole cursor picture, so it belongs to the cleanup worker under its own authorization rather
-- than to any caller that happens to be deleting data. The scope begins false on every connection,
-- so ordinary application work reaches this guard and aborts.
CREATE TRIGGER IF NOT EXISTS owner_deletion_events_guard_delete
BEFORE DELETE ON owner_deletion_events
WHEN arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'An owner-deletion event delete requires owner-cleanup authorization.');
END;
