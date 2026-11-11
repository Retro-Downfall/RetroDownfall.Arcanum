CREATE TRIGGER IF NOT EXISTS guild_cohort_settlement_clock_guard_update
BEFORE UPDATE ON guild_cohort_settlement_clock
WHEN NEW.RootAccountId<>OLD.RootAccountId OR OLD.Revision=9223372036854775807 OR NEW.Revision<>OLD.Revision+1
BEGIN SELECT RAISE(ABORT,'The settlement clock only advances by one committed mutation.'); END;
