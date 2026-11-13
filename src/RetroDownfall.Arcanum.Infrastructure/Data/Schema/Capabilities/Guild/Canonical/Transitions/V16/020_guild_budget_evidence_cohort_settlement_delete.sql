CREATE TRIGGER IF NOT EXISTS guild_budget_evidence_cohort_settlement_delete
AFTER DELETE ON guild_budget_evidence
BEGIN
    INSERT INTO guild_cohort_settlement_clock(RootAccountId,Revision)
    SELECT a.RootAccountId,1 FROM guild_budget_effects e JOIN guild_budget_accounts a ON a.AccountId=e.AccountId WHERE e.EffectId=OLD.EffectId
    ON CONFLICT(RootAccountId) DO UPDATE SET Revision=Revision+1;
END;
