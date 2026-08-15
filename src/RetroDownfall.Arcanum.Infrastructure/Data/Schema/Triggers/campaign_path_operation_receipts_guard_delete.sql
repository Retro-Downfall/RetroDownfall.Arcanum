-- The ledger is what makes a retry idempotent, so a row leaves only with its Campaign owner or under
-- family maintenance, after the matching marker intents are terminal. Both authorizations begin
-- FALSE on every connection, so an unscoped delete reaches this guard and aborts instead of turning
-- a completed operation back into unanswered work that would repeat its filesystem effect.
CREATE TRIGGER IF NOT EXISTS campaign_path_operation_receipts_guard_delete
BEFORE DELETE ON campaign_path_operation_receipts
WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'campaign_path_operation_receipts delete requires an authorized cleanup scope.');
END;
