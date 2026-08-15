-- The identity and destination fields are the recovery owner. They are immutable because recovery
-- reconstructs what to resume from them alone, and a retargeted destination would let a resumed
-- transfer commit into a Session or Campaign nobody asked for. The compare-and-swap keeps two
-- coordinators from advancing the same transfer, and the closed edge list separates the committed
-- path from the abandoned one so a precommit cleanup cannot be mistaken for a committed transfer.
--
-- The disposition rules matter for the same reason. Before ReopenPending nothing has been asked of
-- the gate, so no code may be stored. At ReopenPending exactly one is recorded and it is frozen, so
-- the one-shot finalizer acts on the decision that was actually taken: Completed only after
-- CommitAndReopen, Abandoned only after RollbackAndReopen.
CREATE TRIGGER IF NOT EXISTS protected_session_transfer_intents_guard_update
BEFORE UPDATE ON protected_session_transfer_intents
BEGIN
    SELECT RAISE(ABORT, 'A protected session transfer update requires transfer or family-maintenance authorization.')
    WHERE arcanum_protected_session_transfer_authorized() = 0
        AND arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'A protected session transfer identity, scope, destination, and manifest are immutable.')
    WHERE NEW.OperationId <> OLD.OperationId
        OR NEW.EffectDigest <> OLD.EffectDigest
        OR NEW.SourceEvidenceDigest <> OLD.SourceEvidenceDigest
        OR NEW.DestinationBindingDigest <> OLD.DestinationBindingDigest
        OR NEW.DestinationScopeCode <> OLD.DestinationScopeCode
        OR NEW.DestinationCampaignId IS NOT OLD.DestinationCampaignId
        OR NEW.DestinationSessionId <> OLD.DestinationSessionId
        OR NEW.AttachmentManifestDigest <> OLD.AttachmentManifestDigest
        OR NEW.AttachmentManifestCount <> OLD.AttachmentManifestCount
        OR NEW.DestinationRootIdentityEvidence <> OLD.DestinationRootIdentityEvidence
        OR NEW.CreatedAtUtc <> OLD.CreatedAtUtc;

    SELECT RAISE(ABORT, 'A protected session transfer update requires the exact prior revision.')
    WHERE NEW.Revision <> OLD.Revision + 1;

    SELECT RAISE(ABORT, 'A protected session transfer follows only the committed staging path or a precommit path to ReopenPending.')
    WHERE NOT (
        (OLD.PhaseCode = 1 AND NEW.PhaseCode IN (2, 4))
        OR (OLD.PhaseCode = 2 AND NEW.PhaseCode IN (3, 4))
        OR (OLD.PhaseCode = 3 AND NEW.PhaseCode = 4)
        OR (OLD.PhaseCode = 4 AND NEW.PhaseCode IN (5, 6))
    );

    SELECT RAISE(ABORT, 'A pending disposition is recorded exactly on entry to ReopenPending and is immutable afterward.')
    WHERE NOT (
        (NEW.PhaseCode < 4 AND NEW.PendingDispositionCode IS NULL)
        OR (NEW.PhaseCode = 4 AND OLD.PhaseCode <> 4 AND NEW.PendingDispositionCode IS NOT NULL)
        OR (OLD.PhaseCode = 4 AND NEW.PhaseCode >= 4 AND NEW.PendingDispositionCode IS OLD.PendingDispositionCode)
    );

    SELECT RAISE(ABORT, 'A protected session transfer completes only after CommitAndReopen and is abandoned only after RollbackAndReopen.')
    WHERE (NEW.PhaseCode = 5 AND NEW.PendingDispositionCode <> 2)
        OR (NEW.PhaseCode = 6 AND NEW.PendingDispositionCode <> 1);
END;
