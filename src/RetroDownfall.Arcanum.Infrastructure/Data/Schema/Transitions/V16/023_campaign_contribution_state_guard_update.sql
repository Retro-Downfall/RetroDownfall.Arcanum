CREATE TRIGGER IF NOT EXISTS campaign_contribution_state_guard_update
BEFORE UPDATE ON campaign_contribution_state
WHEN NEW.SessionId IS NOT OLD.SessionId OR NEW.CampaignId IS NOT OLD.CampaignId
    OR NEW.Revision < OLD.Revision OR NEW.SourceGeneration < OLD.SourceGeneration
    OR (NEW.CurrentArtifactId IS NOT NULL AND NEW.CurrentArtifactId IS NOT OLD.CurrentArtifactId
        AND NEW.Revision <> OLD.Revision + 1)
    OR NOT EXISTS (SELECT 1 FROM session_campaign_bindings
    WHERE SessionId = NEW.SessionId AND BindingKindCode = 2 AND CampaignId = NEW.CampaignId)
    OR (NEW.CurrentArtifactId IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM campaign_contribution_artifacts WHERE ArtifactId = NEW.CurrentArtifactId
        AND SessionId = NEW.SessionId AND CampaignId = NEW.CampaignId AND Revision = NEW.Revision
        AND SummarizedThroughSequence = NEW.SummarizedThroughSequence))
BEGIN
    SELECT RAISE(ABORT, 'Campaign contribution publication requires its exact owner, revision, and sequence cursor.');
END;
