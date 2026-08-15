-- Receipts leave only by being folded into aggregates, and folding must happen in contiguous ordinal
-- order so the resulting counts stay meaningful. An ordinary caller deleting a receipt would punch a
-- hole in that order and quietly reduce the accounted disclosure count. Both authorizations begin
-- false on every connection.
CREATE TRIGGER IF NOT EXISTS external_disclosure_receipts_guard_delete
BEFORE DELETE ON external_disclosure_receipts
WHEN arcanum_covenant_family_maintenance_authorized() = 0 AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'A disclosure receipt delete requires an authorized compaction or cleanup scope.');
END;
