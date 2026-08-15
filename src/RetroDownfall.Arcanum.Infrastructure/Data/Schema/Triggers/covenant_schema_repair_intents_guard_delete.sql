-- A nonterminal repair intent is what keeps admission closed until recovery resolves it. Deleting one
-- would reopen the installation with a half-repaired catalog and no owner to finish the job, so only
-- a terminal row may be retained away once its result is no longer replayable.
CREATE TRIGGER IF NOT EXISTS covenant_schema_repair_intents_guard_delete
BEFORE DELETE ON covenant_schema_repair_intents
BEGIN
    SELECT RAISE(ABORT, 'A schema repair intent delete requires family-maintenance authorization.')
    WHERE arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'Only a Completed or Abandoned schema repair intent may be retained away.')
    WHERE OLD.PhaseCode NOT IN (5, 6);
END;
