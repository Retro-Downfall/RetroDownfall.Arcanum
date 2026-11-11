CREATE TRIGGER IF NOT EXISTS guild_portal_cohort_audience_guard_update BEFORE UPDATE ON guild_portal_cohort_audience
BEGIN SELECT RAISE(ABORT,'immutable cohort audience'); END;
