CREATE TRIGGER IF NOT EXISTS guild_cohort_award_authority_scan_guard_update BEFORE UPDATE ON guild_cohort_award_authority_scan
WHEN NEW.CohortId IS NOT OLD.CohortId
BEGIN SELECT RAISE(ABORT,'immutable Cohort authority scan identity'); END;
