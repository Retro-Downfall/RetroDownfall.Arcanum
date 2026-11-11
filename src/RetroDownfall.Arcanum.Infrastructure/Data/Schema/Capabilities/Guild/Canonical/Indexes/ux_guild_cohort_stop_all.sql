CREATE UNIQUE INDEX IF NOT EXISTS ux_guild_cohort_stop_all ON guild_cohort_stop_frontiers(CohortId) WHERE MemberId IS NULL;
