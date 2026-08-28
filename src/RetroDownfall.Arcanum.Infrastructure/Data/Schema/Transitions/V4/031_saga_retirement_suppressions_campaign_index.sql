-- The Campaign-scoped memory reset reads this to take one Campaign's suppressions.
CREATE INDEX IF NOT EXISTS idx_saga_retirement_suppressions_campaign
ON saga_retirement_suppressions(ScopeKindCode, CampaignId);
