CREATE TRIGGER IF NOT EXISTS guild_cohort_content_clock_guard_delete BEFORE DELETE ON guild_cohort_content_clock
BEGIN SELECT RAISE(ABORT,'Cohort content clock cannot be deleted'); END;
