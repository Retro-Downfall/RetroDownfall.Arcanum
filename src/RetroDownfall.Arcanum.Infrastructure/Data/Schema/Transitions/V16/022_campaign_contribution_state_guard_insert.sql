CREATE TRIGGER IF NOT EXISTS campaign_contribution_state_guard_insert
BEFORE INSERT ON campaign_contribution_state
WHEN NEW.CurrentArtifactId IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM campaign_contribution_artifacts WHERE ArtifactId = NEW.CurrentArtifactId
    AND SessionId = NEW.SessionId AND CampaignId = NEW.CampaignId AND Revision = NEW.Revision
    AND SummarizedThroughSequence = NEW.SummarizedThroughSequence)
BEGIN
    SELECT RAISE(ABORT, 'Campaign contribution pointer must bind the exact summarized sequence.');
END;
