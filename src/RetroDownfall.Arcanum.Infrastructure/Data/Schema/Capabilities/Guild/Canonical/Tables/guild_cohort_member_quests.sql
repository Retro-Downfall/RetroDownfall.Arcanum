CREATE TABLE IF NOT EXISTS guild_cohort_member_quests (
    MemberId TEXT NOT NULL REFERENCES guild_cohort_members(Id) ON DELETE RESTRICT,
    QuestId TEXT NOT NULL REFERENCES guild_quests(Id) ON DELETE RESTRICT,
    AssociationKindCode INTEGER NOT NULL CHECK(AssociationKindCode IN (1,2)),
    AssociationSequence INTEGER NOT NULL CHECK(AssociationSequence>=1),
    Active INTEGER NOT NULL CHECK(Active IN (0,1)),
    PRIMARY KEY(MemberId,QuestId),
    UNIQUE(MemberId,AssociationSequence)
);
