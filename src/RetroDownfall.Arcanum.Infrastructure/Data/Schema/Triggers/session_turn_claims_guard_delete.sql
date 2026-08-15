-- A claim is the idempotency record for a request that has already been accepted, so deleting one
-- makes the same client turn ID look new and lets the request run a second time. Claims leave only
-- with their Session, through the guarded retention or owner cleanup transaction that also removes
-- the reservations, guards, and maintenance checkpoints that reference them.
CREATE TRIGGER IF NOT EXISTS session_turn_claims_guard_delete
BEFORE DELETE ON session_turn_claims
WHEN arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'session_turn_claims delete requires an authorized retention or owner cleanup scope.');
END;
