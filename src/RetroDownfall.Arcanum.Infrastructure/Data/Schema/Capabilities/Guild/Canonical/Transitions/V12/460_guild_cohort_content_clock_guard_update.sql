CREATE TRIGGER IF NOT EXISTS guild_cohort_content_clock_guard_update BEFORE UPDATE ON guild_cohort_content_clock
WHEN NEW.Id<>OLD.Id OR OLD.Revision=9223372036854775807 OR NEW.Revision<>OLD.Revision+1
BEGIN SELECT RAISE(ABORT,'invalid Cohort content clock'); END;
