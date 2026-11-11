CREATE TABLE IF NOT EXISTS guild_cohort_stop_frontiers (
    Id TEXT NOT NULL PRIMARY KEY,
    CohortId TEXT NOT NULL REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    MemberId TEXT NULL REFERENCES guild_cohort_members(Id) ON DELETE RESTRICT,
    LastMemberSequence INTEGER NOT NULL DEFAULT 0 CHECK(LastMemberSequence>=0),
    LastAssociationSequence INTEGER NOT NULL DEFAULT 0 CHECK(LastAssociationSequence>=0),
    Completed INTEGER NOT NULL DEFAULT 0 CHECK(Completed IN (0,1)),
    UNIQUE(CohortId,MemberId)
);
