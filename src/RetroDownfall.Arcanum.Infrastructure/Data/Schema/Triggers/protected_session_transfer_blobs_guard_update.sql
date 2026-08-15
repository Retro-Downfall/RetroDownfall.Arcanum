-- Each phase records that one filesystem syscall completed, so recovery restarts at the exact
-- boundary a crash interrupted. That only works if the phases advance one at a time and never
-- regress: a skip would claim a durability barrier nobody executed. Cleaning is the one allowed jump,
-- because proven absence or a verified compare-delete makes every earlier phase moot.
--
-- Identity is filled once, on ParentFsynced to ReopenedVerified, from the same handle that verified
-- the content. Referenced additionally requires the parent to be at DatabaseCommitted, so a child can
-- only be claimed by rows that the same transaction is committing.
CREATE TRIGGER IF NOT EXISTS protected_session_transfer_blobs_guard_update
BEFORE UPDATE ON protected_session_transfer_blobs
BEGIN
    SELECT RAISE(ABORT, 'A protected session transfer blob update requires transfer or family-maintenance authorization.')
    WHERE arcanum_protected_session_transfer_authorized() = 0
        AND arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'A protected session transfer blob identity, location, leaves, and expected content are immutable.')
    WHERE NEW.OperationId <> OLD.OperationId
        OR NEW.BlobOrdinal <> OLD.BlobOrdinal
        OR NEW.DurableParentIdentityEvidence <> OLD.DurableParentIdentityEvidence
        OR NEW.TemporaryLeaf <> OLD.TemporaryLeaf
        OR NEW.FinalLeaf <> OLD.FinalLeaf
        OR NEW.ExpectedContentHash <> OLD.ExpectedContentHash
        OR NEW.ExpectedContentLength <> OLD.ExpectedContentLength
        OR NEW.CreatedAtUtc <> OLD.CreatedAtUtc;

    SELECT RAISE(ABORT, 'A protected session transfer blob update requires the exact prior revision.')
    WHERE NEW.Revision <> OLD.Revision + 1;

    SELECT RAISE(ABORT, 'A protected session transfer blob advances exactly one phase, or is cleaned from any pre-reference phase.')
    WHERE NOT (
        (OLD.PhaseCode BETWEEN 1 AND 7 AND NEW.PhaseCode = OLD.PhaseCode + 1)
        OR (OLD.PhaseCode BETWEEN 1 AND 7 AND NEW.PhaseCode = 9)
    );

    SELECT RAISE(ABORT, 'An observed physical identity is filled exactly once on ParentFsynced to ReopenedVerified and never changes afterward.')
    WHERE NOT (
        (
            OLD.PhaseCode = 6
            AND NEW.PhaseCode = 7
            AND OLD.ObservedPhysicalIdentity IS NULL
            AND NEW.ObservedPhysicalIdentity IS NOT NULL
        )
        OR (
            NOT (OLD.PhaseCode = 6 AND NEW.PhaseCode = 7)
            AND NEW.ObservedPhysicalIdentity IS OLD.ObservedPhysicalIdentity
        )
    );

    SELECT RAISE(ABORT, 'A protected session transfer blob becomes Referenced only while its parent is DatabaseCommitted.')
    WHERE NEW.PhaseCode = 8
        AND NOT EXISTS (
            SELECT 1
            FROM protected_session_transfer_intents
            WHERE OperationId = OLD.OperationId
                AND PhaseCode = 3
        );
END;
