-- Deleting a reservation removes the ledger row its counters were incremented for, so an unscoped
-- delete would leave the quota state describing slots that no longer have an owner. The two legal
-- callers are the quota guard performing an authorized capacity correction and the Session retention
-- or owner cleanup transaction that removes the whole Session after decrementing the installation
-- counters by that Session's exact locked values.
CREATE TRIGGER IF NOT EXISTS assistant_finalization_capacity_reservations_guard_delete
BEFORE DELETE ON assistant_finalization_capacity_reservations
WHEN arcanum_turn_capacity_mutation_authorized() = 0
    AND arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'assistant_finalization_capacity_reservations delete requires an authorized capacity or retention scope.');
END;
