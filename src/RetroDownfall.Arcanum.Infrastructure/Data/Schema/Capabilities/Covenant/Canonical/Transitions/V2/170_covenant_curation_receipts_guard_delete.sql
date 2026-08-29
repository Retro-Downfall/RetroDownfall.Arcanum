-- A curation receipt outlives the change it describes for the same reason a mutation receipt does: it
-- is the answer a replay resolves through. Removing one is authorized cleanup work or nothing.
CREATE TRIGGER IF NOT EXISTS covenant_curation_receipts_guard_delete
BEFORE DELETE ON covenant_curation_receipts
WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_curation_receipts delete requires an authorized cleanup scope.');
END;
