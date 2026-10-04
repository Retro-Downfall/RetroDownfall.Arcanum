-- Replaces the version-5 guard with the head text, which also admits entry erasure. A trigger is a
-- whole object and the drift gate compares its installed text against the head tree, so the older
-- text cannot stay beside the new one.
DROP TRIGGER IF EXISTS covenant_search_outbox_guard_delete;

CREATE TRIGGER IF NOT EXISTS covenant_search_outbox_guard_delete
BEFORE DELETE ON covenant_search_outbox
WHEN arcanum_accelerator_sync_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
    AND arcanum_covenant_entry_erasure_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_search_outbox delete requires an authorized synchronization scope.');
END;
