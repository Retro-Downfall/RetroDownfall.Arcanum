-- The reservation ledger and the counters that bound it are only meaningful if they move together,
-- so every insert must come from the narrow quota guard that holds one TurnCapacityMutation scope
-- across the whole multi-table sequence. That scope begins FALSE on every connection, so direct SQL
-- cannot mint capacity, and the trigger can still permit the intermediate counter states the guard
-- unavoidably passes through.
--
-- The state a row may be born in is decided by its origin. A public claim reserves a slot it will
-- consume later, so it starts Reserved. Internal begin and imported or forked guard creation have no
-- waiting period at all: their guarded row is written in the same transaction, so they start
-- Consumed. Nothing may be born Released, because releasing capacity that was never reserved would
-- let a caller drive the counters down without ever having driven them up.
CREATE TRIGGER IF NOT EXISTS assistant_finalization_capacity_reservations_validate_insert
BEFORE INSERT ON assistant_finalization_capacity_reservations
BEGIN
    SELECT RAISE(ABORT, 'A finalization capacity reservation requires an authorized turn capacity mutation scope.')
    WHERE arcanum_turn_capacity_mutation_authorized() = 0;

    SELECT RAISE(ABORT, 'A public claim reservation is born reserved.')
    WHERE NEW.OriginCode = 1 AND NEW.StateCode <> 1;

    SELECT RAISE(ABORT, 'An internal, imported, or forked reservation is born consumed beside its guarded row.')
    WHERE NEW.OriginCode IN (2, 3, 4) AND NEW.StateCode <> 2;
END;
