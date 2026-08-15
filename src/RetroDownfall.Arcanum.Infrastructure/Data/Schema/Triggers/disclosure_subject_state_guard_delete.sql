-- The lifecycle row is the proof that a subject's receipts and aggregates were fully folded and
-- joined. Removing it while either still exists would orphan them, and nothing would ever fold them
-- again. Compaction deletes it last, under its own authorization, after proving nothing remains.
CREATE TRIGGER IF NOT EXISTS disclosure_subject_state_guard_delete
BEFORE DELETE ON disclosure_subject_state
WHEN arcanum_covenant_family_maintenance_authorized() = 0 AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'A disclosure subject state delete requires an authorized compaction or cleanup scope.');
END;
