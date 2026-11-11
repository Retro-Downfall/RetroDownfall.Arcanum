CREATE TRIGGER IF NOT EXISTS guild_cohort_selections_guard_update BEFORE UPDATE ON guild_cohort_selections
BEGIN SELECT RAISE(ABORT,'immutable cohort selection'); END;
