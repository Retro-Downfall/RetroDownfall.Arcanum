CREATE UNIQUE INDEX IF NOT EXISTS ux_guild_cohort_active_quest ON guild_cohort_member_quests(QuestId) WHERE Active=1;
