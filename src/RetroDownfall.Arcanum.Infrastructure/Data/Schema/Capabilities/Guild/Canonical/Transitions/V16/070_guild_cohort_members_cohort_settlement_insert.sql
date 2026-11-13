CREATE TRIGGER IF NOT EXISTS guild_cohort_members_cohort_settlement_insert
AFTER INSERT ON guild_cohort_members
BEGIN
    INSERT INTO guild_cohort_settlement_clock(RootAccountId,Revision)
    SELECT a.RootAccountId,1 FROM guild_cohorts h JOIN guild_budget_accounts a ON a.AccountId=h.AllocationAccountId WHERE h.Id=NEW.CohortId
    ON CONFLICT(RootAccountId) DO UPDATE SET Revision=Revision+1;
END;
