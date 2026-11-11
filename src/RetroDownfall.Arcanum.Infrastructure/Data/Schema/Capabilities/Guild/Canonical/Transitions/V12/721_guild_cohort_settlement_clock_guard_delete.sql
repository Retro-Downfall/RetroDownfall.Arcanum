CREATE TRIGGER IF NOT EXISTS guild_cohort_settlement_clock_guard_delete
BEFORE DELETE ON guild_cohort_settlement_clock
WHEN EXISTS(SELECT 1 FROM guild_budget_accounts WHERE RootAccountId=OLD.RootAccountId)
BEGIN SELECT RAISE(ABORT,'Live Cohort settlement clock cannot be deleted'); END;
