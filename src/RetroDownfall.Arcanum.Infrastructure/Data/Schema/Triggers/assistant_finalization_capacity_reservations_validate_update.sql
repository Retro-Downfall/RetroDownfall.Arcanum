-- Only two transitions exist, and each one is paired with a counter move the same authorized scope
-- performs: assistant begin turns Reserved into Consumed while inserting the placeholder, and a
-- never-begun terminal claim turns Reserved into Released. Both destinations are terminal, so a
-- replayed begin or release finds the row already moved and cannot double count. Reopening a
-- Consumed row would hand back a slot that a finalization guard is still using; reopening a Released
-- one would let a terminated claim take capacity back after it gave it up.
--
-- The identity fields never change, because they are what the placeholder, the guard insert, and the
-- claim all match against. A reservation that could be rebound to another Session or another future
-- assistant identity would move a counted slot to an owner that never paid for it.
CREATE TRIGGER IF NOT EXISTS assistant_finalization_capacity_reservations_validate_update
BEFORE UPDATE ON assistant_finalization_capacity_reservations
BEGIN
    SELECT RAISE(ABORT, 'A finalization capacity reservation requires an authorized turn capacity mutation scope.')
    WHERE arcanum_turn_capacity_mutation_authorized() = 0;

    SELECT RAISE(ABORT, 'A finalization capacity reservation cannot change its identity or its owner.')
    WHERE NEW.ReservationId <> OLD.ReservationId
        OR NEW.SessionId <> OLD.SessionId
        OR NEW.AssistantEntryId <> OLD.AssistantEntryId
        OR NEW.OriginCode <> OLD.OriginCode
        OR NEW.ClaimId IS NOT OLD.ClaimId
        OR NEW.CreatedAtUtc <> OLD.CreatedAtUtc;

    SELECT RAISE(ABORT, 'A consumed or released finalization capacity reservation is terminal.')
    WHERE OLD.StateCode IN (2, 3);

    SELECT RAISE(ABORT, 'A reserved finalization slot may only be consumed or released.')
    WHERE OLD.StateCode = 1 AND NEW.StateCode NOT IN (2, 3);
END;
