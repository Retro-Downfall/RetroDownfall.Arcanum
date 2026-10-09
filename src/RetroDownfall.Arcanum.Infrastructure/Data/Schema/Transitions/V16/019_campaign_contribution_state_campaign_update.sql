-- New, unconsumed Sessions add incremental debt; changing a consumed source requires a refold.
CREATE TRIGGER IF NOT EXISTS campaign_contribution_state_campaign_update
AFTER UPDATE ON campaign_contribution_state
WHEN NEW.CurrentArtifactId IS NOT OLD.CurrentArtifactId
BEGIN
    UPDATE campaign_rollup_state
    SET CurrentArtifactId = CASE WHEN EXISTS (SELECT 1 FROM campaign_rollup_sources source
            WHERE source.RollupArtifactId = campaign_rollup_state.CurrentArtifactId AND source.SessionId = OLD.SessionId) THEN NULL ELSE CurrentArtifactId END,
        RefoldRequired = CASE WHEN EXISTS (SELECT 1 FROM campaign_rollup_sources source
            WHERE source.RollupArtifactId = campaign_rollup_state.CurrentArtifactId AND source.SessionId = OLD.SessionId) THEN 1 ELSE RefoldRequired END,
        SourceGeneration = SourceGeneration + 1,
        LastFoldedSessionId = CASE WHEN EXISTS (SELECT 1 FROM campaign_rollup_sources source
            WHERE source.RollupArtifactId = campaign_rollup_state.CurrentArtifactId AND source.SessionId = OLD.SessionId) THEN NULL ELSE LastFoldedSessionId END
    WHERE CampaignId = OLD.CampaignId;
END;
