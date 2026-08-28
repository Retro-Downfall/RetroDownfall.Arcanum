-- Selecting one Campaign's suppressions by scope and Campaign.
CREATE INDEX IF NOT EXISTS idx_saga_retirement_suppressions_campaign
ON saga_retirement_suppressions(ScopeKindCode, CampaignId);
