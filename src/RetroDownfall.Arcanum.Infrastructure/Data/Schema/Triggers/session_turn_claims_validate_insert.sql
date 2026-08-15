-- A claim is only admissible at the very start of a turn, and only once its future finalization slot
-- already exists. Requiring the reservation to be Reserved, owned by this same Session, and bound to
-- this same claim is what makes capacity honest: a claim inserted beside someone else's reservation,
-- or beside one that was already consumed or released, would run a turn against a guard slot it does
-- not own. The origin check falls out of the same lookup, because only a PublicClaim reservation
-- carries a claim identity.
--
-- The step mask and checkpoint revision start at zero because a brand-new claim has completed no
-- maintenance. Accepting a nonzero pair would let a caller declare work done that never ran and skip
-- straight past the checkpoints recovery relies on.
CREATE TRIGGER IF NOT EXISTS session_turn_claims_validate_insert
BEFORE INSERT ON session_turn_claims
BEGIN
    SELECT RAISE(ABORT, 'A new session turn claim must begin in the pending maintenance state.')
    WHERE NEW.StateCode <> 1;

    SELECT RAISE(ABORT, 'A new session turn claim has completed no maintenance and starts with a zero step mask and checkpoint revision.')
    WHERE NEW.CompletedStepMask <> 0 OR NEW.CheckpointRevision <> 0;

    -- The frozen input evidence and the expected current projection start identical; only the
    -- guarded maintenance transaction may make them diverge afterward.
    SELECT RAISE(ABORT, 'A new session turn claim must expect the sensitivity revision it froze as input.')
    WHERE NEW.ExpectedCurrentSensitivityRevision <> NEW.InputSensitivityRevision;

    SELECT RAISE(ABORT, 'A session turn claim requires its own reserved finalization capacity in the same Session.')
    WHERE NOT EXISTS (
        SELECT 1
        FROM assistant_finalization_capacity_reservations
        WHERE ReservationId = NEW.FinalizationReservationId
            AND SessionId = NEW.SessionId
            AND ClaimId = NEW.ClaimId
            AND OriginCode = 1
            AND StateCode = 1
    );
END;
