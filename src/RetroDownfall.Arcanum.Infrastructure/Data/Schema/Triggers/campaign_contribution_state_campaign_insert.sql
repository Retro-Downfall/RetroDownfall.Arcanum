CREATE TRIGGER IF NOT EXISTS campaign_contribution_state_campaign_insert
AFTER INSERT ON campaign_contribution_state
BEGIN
    UPDATE campaign_rollup_state SET SourceGeneration = SourceGeneration + 1
    WHERE CampaignId = NEW.CampaignId;
END;
