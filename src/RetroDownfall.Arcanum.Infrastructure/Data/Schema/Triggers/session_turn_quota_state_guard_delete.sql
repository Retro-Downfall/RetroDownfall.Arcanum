-- The counter row is the Session's capacity owner, so removing it while the Session lives would
-- hand that Session a fresh unmetered ceiling on its next claim. It leaves only with its Session,
-- through the retention or owner cleanup transaction that has already decremented the installation
-- counters by these exact values, or through an authorized capacity correction.
CREATE TRIGGER IF NOT EXISTS session_turn_quota_state_guard_delete
BEFORE DELETE ON session_turn_quota_state
WHEN arcanum_turn_capacity_mutation_authorized() = 0
    AND arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'session_turn_quota_state delete requires an authorized capacity or retention scope.');
END;
