CREATE TRIGGER IF NOT EXISTS guild_dispatches_cohort_settlement_delete
AFTER DELETE ON guild_dispatches
BEGIN
    INSERT INTO guild_cohort_settlement_clock(RootAccountId,Revision)
    SELECT a.RootAccountId,1 FROM guild_quests q JOIN guild_budget_accounts a ON a.AccountId=q.AllocationAccountId WHERE q.Id=OLD.QuestId
    ON CONFLICT(RootAccountId) DO UPDATE SET Revision=Revision+1;
END;
