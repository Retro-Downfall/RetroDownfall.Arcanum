CREATE TABLE IF NOT EXISTS guild_portal_cohort_audience (
    EnvelopeId TEXT NOT NULL REFERENCES guild_envelopes(Id) ON DELETE RESTRICT,
    MemberId TEXT NOT NULL,
    QuestId TEXT NOT NULL,
    PRIMARY KEY(EnvelopeId,MemberId,QuestId),
    FOREIGN KEY(MemberId,QuestId) REFERENCES guild_cohort_member_quests(MemberId,QuestId) ON DELETE RESTRICT
);
