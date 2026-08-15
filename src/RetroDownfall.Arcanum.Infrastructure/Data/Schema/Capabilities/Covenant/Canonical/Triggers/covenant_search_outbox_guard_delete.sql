-- The outbox is the accelerator's work queue. A row leaves it only when the synchronization worker
-- has applied it, in the same transaction that advances the applied FTS tuple, or when family
-- maintenance is tearing the dataset down. Any other delete would drop a projection delta while the
-- applied tuple still claims the sequence was published, and search would keep serving text that
-- canonical no longer holds. Both authorizations begin FALSE on every connection, so ordinary
-- application work aborts here.
CREATE TRIGGER IF NOT EXISTS covenant_search_outbox_guard_delete
BEFORE DELETE ON covenant_search_outbox
WHEN arcanum_accelerator_sync_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_search_outbox delete requires an authorized synchronization scope.');
END;
