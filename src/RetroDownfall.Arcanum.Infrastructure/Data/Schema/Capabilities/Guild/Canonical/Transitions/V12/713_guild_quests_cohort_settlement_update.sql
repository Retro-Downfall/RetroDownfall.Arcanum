CREATE TRIGGER IF NOT EXISTS guild_quests_cohort_settlement_update
AFTER UPDATE ON guild_quests
BEGIN
    INSERT INTO guild_cohort_settlement_clock(RootAccountId,Revision)
    SELECT RootAccountId,1 FROM guild_budget_accounts WHERE AccountId=NEW.AllocationAccountId
    ON CONFLICT(RootAccountId) DO UPDATE SET Revision=Revision+1;
END;
