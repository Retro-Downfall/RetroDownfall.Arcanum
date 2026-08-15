-- Turn receipts leave in exactly two ways: owner cleanup of the Session or Campaign that produced
-- them, and Covenant family maintenance folding the oldest end of the bounded per-Session tail into
-- the aggregate. Any other delete would drop turns the aggregate has not yet accounted for. Both
-- authorizations begin FALSE on every connection, so ordinary application work aborts here.
CREATE TRIGGER IF NOT EXISTS covenant_turn_receipts_guard_delete
BEFORE DELETE ON covenant_turn_receipts
WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_turn_receipts delete requires an authorized cleanup scope.');
END;
