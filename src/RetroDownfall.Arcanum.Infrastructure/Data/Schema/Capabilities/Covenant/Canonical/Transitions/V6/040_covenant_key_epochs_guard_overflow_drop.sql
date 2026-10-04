-- The overflow guard refuses every update that does not advance KeyEpoch, and the backfill below
-- leaves KeyEpoch where it is. The guard is set aside for that one statement and recreated from the
-- head text straight after it, inside the same step transaction.
DROP TRIGGER IF EXISTS covenant_key_epochs_guard_overflow;
