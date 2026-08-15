-- Deleting the parent takes its blob children with it. Doing that before the gate disposition
-- succeeded would discard the journal that recovery needs to find and clean up staged files, leaving
-- orphaned attachment blobs on disk with nothing left to name them.
CREATE TRIGGER IF NOT EXISTS protected_session_transfer_intents_guard_delete
BEFORE DELETE ON protected_session_transfer_intents
BEGIN
    SELECT RAISE(ABORT, 'A protected session transfer delete requires transfer or family-maintenance authorization.')
    WHERE arcanum_protected_session_transfer_authorized() = 0
        AND arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'Only a Completed or Abandoned protected session transfer may be deleted.')
    WHERE OLD.PhaseCode NOT IN (5, 6);
END;
