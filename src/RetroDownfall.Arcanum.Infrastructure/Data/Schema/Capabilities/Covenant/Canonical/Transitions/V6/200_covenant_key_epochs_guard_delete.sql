-- A key's epoch row is deleted only by owner cleanup, Covenant family maintenance, or an entry erasure
-- that reclaims the key, and each of those deletes every curation row for the key in the same
-- transaction. Deleting the row alone would reset the key's binding epoch to 0 underneath curation
-- recorded against it. All three authorizations begin FALSE on every connection, so ordinary
-- application work reaches this guard and aborts.
CREATE TRIGGER IF NOT EXISTS covenant_key_epochs_guard_delete
BEFORE DELETE ON covenant_key_epochs
WHEN arcanum_owner_cleanup_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
    AND arcanum_covenant_entry_erasure_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_key_epochs delete requires an authorized cleanup scope.');
END;
