-- The per-Session counters are only trustworthy if they move in the exact shapes the reservation
-- ledger can produce, so the four closed transitions are spelled out rather than left to whatever
-- arithmetic a caller writes. Reserving a public claim takes one claim and one reserved slot
-- together. Consuming that reservation moves one count from reserved to consumed and leaves total
-- guard capacity unchanged, which is why the ceiling applies to the sum. Releasing a never-begun
-- reservation gives back only the reserved slot and never the claim, because the claim really was
-- made. A direct internal, imported, or forked guard was never reserved and increments consumed
-- alone.
--
-- Nothing here decreases the claim count. Lifetime claim capacity is deliberately not returned by a
-- terminal claim: a Session that could recycle claims by finishing them would have no ceiling at
-- all. Whole-Session retention deletes this row instead of zeroing it.
CREATE TRIGGER IF NOT EXISTS session_turn_quota_state_validate_update
BEFORE UPDATE ON session_turn_quota_state
BEGIN
    SELECT RAISE(ABORT, 'A session turn quota update requires an authorized turn capacity mutation scope.')
    WHERE arcanum_turn_capacity_mutation_authorized() = 0;

    SELECT RAISE(ABORT, 'A session turn quota row cannot change the Session it counts for.')
    WHERE NEW.SessionId <> OLD.SessionId;

    SELECT RAISE(ABORT, 'session_turn_quota_state accepts only its closed reserve, consume, release, and direct guard transitions.')
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
    );
END;
