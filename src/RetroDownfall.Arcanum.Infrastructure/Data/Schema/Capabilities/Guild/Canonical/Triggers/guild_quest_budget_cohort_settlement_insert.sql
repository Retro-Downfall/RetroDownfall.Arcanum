CREATE TRIGGER IF NOT EXISTS guild_quest_budget_cohort_settlement_insert
AFTER INSERT ON guild_quest_budget
BEGIN
    INSERT INTO guild_cohort_settlement_clock(RootAccountId,Revision)
    SELECT RootAccountId,1 FROM guild_budget_accounts WHERE AccountId=NEW.AccountId
    ON CONFLICT(RootAccountId) DO UPDATE SET Revision=Revision+1;
END;
