CREATE TRIGGER IF NOT EXISTS guild_cohort_eval_candidates_guard_update BEFORE UPDATE ON guild_cohort_eval_candidates
BEGIN SELECT RAISE(ABORT,'immutable cohort evaluation candidate'); END;
