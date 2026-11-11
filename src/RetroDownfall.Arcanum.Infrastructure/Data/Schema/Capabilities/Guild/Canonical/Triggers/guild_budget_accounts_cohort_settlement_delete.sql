CREATE TRIGGER IF NOT EXISTS guild_budget_accounts_cohort_settlement_delete
AFTER DELETE ON guild_budget_accounts
BEGIN
    INSERT INTO guild_cohort_settlement_clock(RootAccountId,Revision)
    SELECT OLD.RootAccountId,1 WHERE true
    ON CONFLICT(RootAccountId) DO UPDATE SET Revision=Revision+1;
END;
