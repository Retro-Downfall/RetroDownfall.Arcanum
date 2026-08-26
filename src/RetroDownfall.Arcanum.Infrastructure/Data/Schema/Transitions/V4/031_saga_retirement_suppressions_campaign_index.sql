-- Campaign cleanup reads this to remove a deleted Campaign's suppressions in the transaction that
-- removes the Campaign.
CREATE INDEX IF NOT EXISTS idx_saga_retirement_suppressions_campaign
ON saga_retirement_suppressions(ScopeKindCode, CampaignId);
