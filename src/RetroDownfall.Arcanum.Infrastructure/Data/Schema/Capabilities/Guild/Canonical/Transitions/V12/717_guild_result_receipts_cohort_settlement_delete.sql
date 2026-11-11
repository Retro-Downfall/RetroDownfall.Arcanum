CREATE TRIGGER IF NOT EXISTS guild_result_receipts_cohort_settlement_delete
AFTER DELETE ON guild_result_receipts
BEGIN
    INSERT INTO guild_cohort_settlement_clock(RootAccountId,Revision)
    SELECT a.RootAccountId,1 FROM guild_budget_accounts a JOIN guild_quests q ON q.AllocationAccountId=a.AccountId WHERE q.Id=OLD.QuestId
    ON CONFLICT(RootAccountId) DO UPDATE SET Revision=Revision+1;
END;
