-- The installation counters take the same four closed increments as a Session's, because they are
-- incremented by the same guard in the same transaction. They accept one shape a Session row never
-- does: whole-Session retention gives lifetime capacity back installation-wide, decrementing each
-- counter by the exact values locked from the Session row it is about to delete. That release is
-- expressed here as counters that only move down, never up, in a single statement, so an authorized
-- retention cannot disguise a fresh grant as a cleanup. A statement that changed nothing is refused
-- as well, because every legal caller here is committing a counter move it has already decided on.
CREATE TRIGGER IF NOT EXISTS installation_turn_quota_state_validate_update
BEFORE UPDATE ON installation_turn_quota_state
BEGIN
    SELECT RAISE(ABORT, 'An installation turn quota update requires an authorized turn capacity mutation scope.')
    WHERE arcanum_turn_capacity_mutation_authorized() = 0;

    SELECT RAISE(ABORT, 'The installation turn quota row is a singleton and its state key cannot change.')
    WHERE NEW.StateKey <> 1;

    SELECT RAISE(ABORT, 'installation_turn_quota_state accepts only its closed capacity transitions and whole-Session release.')
    WHERE NOT (
        (NEW.ClaimCount = OLD.ClaimCount + 1
            AND NEW.ReservedFinalizationCount = OLD.ReservedFinalizationCount + 1
            AND NEW.ConsumedFinalizationCount = OLD.ConsumedFinalizationCount)
        OR (NEW.ClaimCount = OLD.ClaimCount
            AND NEW.ReservedFinalizationCount = OLD.ReservedFinalizationCount - 1
            AND NEW.ConsumedFinalizationCount = OLD.ConsumedFinalizationCount + 1)
        OR (NEW.ClaimCount = OLD.ClaimCount
            AND NEW.ReservedFinalizationCount = OLD.ReservedFinalizationCount - 1
            AND NEW.ConsumedFinalizationCount = OLD.ConsumedFinalizationCount)
        OR (NEW.ClaimCount = OLD.ClaimCount
            AND NEW.ReservedFinalizationCount = OLD.ReservedFinalizationCount
            AND NEW.ConsumedFinalizationCount = OLD.ConsumedFinalizationCount + 1)
        OR (NEW.ClaimCount <= OLD.ClaimCount
            AND NEW.ReservedFinalizationCount <= OLD.ReservedFinalizationCount
            AND NEW.ConsumedFinalizationCount <= OLD.ConsumedFinalizationCount
            AND (NEW.ClaimCount < OLD.ClaimCount
                OR NEW.ReservedFinalizationCount < OLD.ReservedFinalizationCount
                OR NEW.ConsumedFinalizationCount < OLD.ConsumedFinalizationCount))
    );
END;
