CREATE TRIGGER IF NOT EXISTS guild_cohort_ledger_guard_update BEFORE UPDATE ON guild_cohort_ledger
BEGIN SELECT RAISE(ABORT,'Cohort notes are immutable'); END;
