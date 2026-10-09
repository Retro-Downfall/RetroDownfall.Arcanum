CREATE TRIGGER IF NOT EXISTS campaign_rollup_state_guard_update
BEFORE UPDATE ON campaign_rollup_state
WHEN NEW.CampaignId IS NOT OLD.CampaignId
    OR NEW.Revision < OLD.Revision
    OR NEW.SourceGeneration < OLD.SourceGeneration
    OR (NEW.CurrentArtifactId IS NOT NULL AND NEW.CurrentArtifactId IS NOT OLD.CurrentArtifactId
        AND NEW.Revision <> OLD.Revision + 1)
BEGIN
    SELECT RAISE(ABORT, 'Campaign publication requires a monotonic revision and source generation.');
END;
