CREATE TRIGGER IF NOT EXISTS campaign_rollup_sources_campaign_delete
AFTER DELETE ON campaign_rollup_sources
BEGIN
    UPDATE campaign_rollup_state
    SET CurrentArtifactId = NULL, RefoldRequired = 1, SourceGeneration = SourceGeneration + 1, LastFoldedSessionId = NULL
    WHERE CampaignId = OLD.CampaignId AND CurrentArtifactId = OLD.RollupArtifactId;
END;
