CREATE TRIGGER IF NOT EXISTS campaign_maintenance_checkpoints_guard_update
BEFORE UPDATE ON campaign_maintenance_checkpoints
WHEN OLD.CheckpointStateCode = 2
    OR NEW.ClaimId IS NOT OLD.ClaimId OR NEW.StepCode IS NOT OLD.StepCode
    OR NEW.CampaignId IS NOT OLD.CampaignId OR NEW.SessionId IS NOT OLD.SessionId
    OR NEW.InputSourceGeneration IS NOT OLD.InputSourceGeneration OR NEW.InputManifestDigest IS NOT OLD.InputManifestDigest
    OR NEW.SourceSessionId IS NOT OLD.SourceSessionId OR NEW.ExpectedOutputRevision IS NOT OLD.ExpectedOutputRevision
    OR NEW.PhysicalProviderAttemptOrdinal < OLD.PhysicalProviderAttemptOrdinal
    OR (NEW.ExecutorId IS NOT OLD.ExecutorId AND NOT EXISTS (SELECT 1 FROM session_turn_claims
        WHERE ClaimId = NEW.ClaimId AND StateCode = 1 AND ExecutorId = NEW.ExecutorId))
    OR NEW.CheckpointRevision <> OLD.CheckpointRevision + 1
    OR (OLD.CheckpointStateCode = 3 AND NEW.CheckpointStateCode <> 3)
BEGIN
    SELECT RAISE(ABORT, 'Campaign maintenance input identity is immutable and publication is compare-and-swap.');
END;
