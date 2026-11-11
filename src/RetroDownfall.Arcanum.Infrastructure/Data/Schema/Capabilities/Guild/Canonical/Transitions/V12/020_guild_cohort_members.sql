CREATE TABLE IF NOT EXISTS guild_cohort_members (
    Id TEXT NOT NULL PRIMARY KEY,
    CohortId TEXT NOT NULL REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    AgentId TEXT NOT NULL,
    PrimaryVotingQuestId TEXT NOT NULL UNIQUE REFERENCES guild_quests(Id) ON DELETE RESTRICT,
    AdmissionSequence INTEGER NOT NULL CHECK(AdmissionSequence>=1),
    StateCode INTEGER NOT NULL CHECK(StateCode IN (1,2)),
    RemovalSequence INTEGER NULL CHECK(RemovalSequence IS NULL OR RemovalSequence>AdmissionSequence),
    UNIQUE(CohortId,AgentId),
    UNIQUE(CohortId,AdmissionSequence),
    CHECK((StateCode=1 AND RemovalSequence IS NULL) OR (StateCode=2 AND RemovalSequence IS NOT NULL))
);
