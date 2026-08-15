-- A subject aggregate holds folded counts that have not yet been joined into the installation-wide
-- state. Deleting one before that join loses every receipt it absorbed, and no detail row survives to
-- recompute it. Compaction removes these only in the transaction that already succeeded at joining
-- them.
CREATE TRIGGER IF NOT EXISTS disclosure_subject_aggregates_guard_delete
BEFORE DELETE ON disclosure_subject_aggregates
WHEN arcanum_covenant_family_maintenance_authorized() = 0 AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'A disclosure subject aggregate delete requires an authorized compaction or cleanup scope.');
END;
