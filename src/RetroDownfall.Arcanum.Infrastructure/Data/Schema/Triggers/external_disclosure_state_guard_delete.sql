-- This is the one table that answers "has anything ever left this installation for that
-- destination". Its rows are seeded once and joined into forever; there is no authorization that
-- should be able to clear an EverOccurred bit or reduce a lower-bound count, because either would
-- turn a truthful disclosure record into a false denial. Retention may fold detail, and a full
-- installation reset removes the Grimoire itself, but no delete statement reaches this row.
CREATE TRIGGER IF NOT EXISTS external_disclosure_state_guard_delete
BEFORE DELETE ON external_disclosure_state
BEGIN
    SELECT RAISE(ABORT, 'external_disclosure_state rows are permanent; joined disclosure state cannot be deleted.');
END;
