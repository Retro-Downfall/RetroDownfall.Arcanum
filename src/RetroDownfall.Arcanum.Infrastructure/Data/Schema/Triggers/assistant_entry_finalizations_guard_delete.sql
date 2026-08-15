-- The guard deliberately outlives the placeholder it guards: a Discarded Entry is deleted while its
-- guard stays behind so the retry still resolves. It leaves only when its whole Session leaves,
-- through Session retention or owner cleanup. Deleting it earlier would turn a terminal turn back
-- into a pending one and let the same request run a second time.
CREATE TRIGGER IF NOT EXISTS assistant_entry_finalizations_guard_delete
BEFORE DELETE ON assistant_entry_finalizations
WHEN arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'assistant_entry_finalizations delete requires an authorized retention or owner cleanup scope.');
END;
