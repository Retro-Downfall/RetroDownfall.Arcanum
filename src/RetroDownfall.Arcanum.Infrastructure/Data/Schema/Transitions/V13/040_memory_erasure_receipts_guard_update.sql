-- A receipt is the record of one erasure, so every column except the two scrub columns is fixed when it
-- is written. The scrub columns make exactly two moves: the WalCheckpointPending bit clears once a
-- checkpoint truncates the WAL, and Pending becomes Verified once no reason remains. The other reasons
-- are not upgradable, so a receipt that carries one can never read Verified, and Verified never returns
-- to Pending. The guard fires before the table's checks, so every update it refuses reports its message.
-- An update it permits can still fail a table check: clearing the last reason must move the receipt to
-- Verified in the same statement, because a Pending receipt always names a reason.
CREATE TRIGGER IF NOT EXISTS memory_erasure_receipts_guard_update
BEFORE UPDATE ON memory_erasure_receipts
WHEN NEW.MutationId IS NOT OLD.MutationId
    OR NEW.StoreCode IS NOT OLD.StoreCode
    OR NEW.KeyId IS NOT OLD.KeyId
    OR NEW.RequestDigest IS NOT OLD.RequestDigest
    OR NEW.EffectDigest IS NOT OLD.EffectDigest
    OR NEW.ErasedItemCount IS NOT OLD.ErasedItemCount
    OR NEW.RemovedRowCount IS NOT OLD.RemovedRowCount
    OR NEW.RemovedLabelCount IS NOT OLD.RemovedLabelCount
    OR NEW.RemovedRetirementSuppressionCount IS NOT OLD.RemovedRetirementSuppressionCount
    OR NEW.AuthorshipEvidenceCode IS NOT OLD.AuthorshipEvidenceCode
    OR NEW.ContextEvidenceCode IS NOT OLD.ContextEvidenceCode
    OR NEW.EmbeddingEvidenceCode IS NOT OLD.EmbeddingEvidenceCode
    OR NEW.BackupEvidenceCode IS NOT OLD.BackupEvidenceCode
    OR NEW.OtherExternalEvidenceCode IS NOT OLD.OtherExternalEvidenceCode
    OR NEW.RetainedCopiesMask IS NOT OLD.RetainedCopiesMask
    OR NEW.ScrubPendingReasonMask NOT IN (OLD.ScrubPendingReasonMask, OLD.ScrubPendingReasonMask & ~1)
    OR (NEW.ScrubStateCode IS NOT OLD.ScrubStateCode
        AND NOT (OLD.ScrubStateCode = 1 AND NEW.ScrubStateCode = 2 AND NEW.ScrubPendingReasonMask = 0))
BEGIN
    SELECT RAISE(ABORT, 'memory_erasure_receipts permits only clearing the WAL checkpoint reason and moving Pending to Verified once no reason remains.');
END;
