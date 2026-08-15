-- A blob row is the only record of a file this operation may have created, so it survives until the
-- parent has reached a post-disposition terminal phase. Under Abandoned the child must already be
-- Cleaned: a Referenced child was handed to committed Session rows, and deleting its journal entry
-- under an abandonment would leave a live attachment nothing accounts for.
--
-- The parent check is written as a negated EXISTS so that a cascade from an already-deleted parent
-- still passes. The parent delete guard has already enforced the terminal phase at that point, and
-- requiring the parent to still be present here would only break the cascade it authorized.
CREATE TRIGGER IF NOT EXISTS protected_session_transfer_blobs_guard_delete
BEFORE DELETE ON protected_session_transfer_blobs
BEGIN
    SELECT RAISE(ABORT, 'A protected session transfer blob delete requires transfer or family-maintenance authorization.')
    WHERE arcanum_protected_session_transfer_authorized() = 0
        AND arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'A protected session transfer blob is removed with a Completed parent, or with an Abandoned parent only when it is already Cleaned.')
    WHERE EXISTS (
        SELECT 1
        FROM protected_session_transfer_intents
        WHERE OperationId = OLD.OperationId
            AND NOT (
                PhaseCode = 5
                OR (PhaseCode = 6 AND OLD.PhaseCode = 9)
            )
    );
END;
