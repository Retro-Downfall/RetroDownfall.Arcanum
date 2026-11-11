CREATE TABLE IF NOT EXISTS guild_cohort_contributions (
    CohortId TEXT NOT NULL REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    MemberId TEXT NOT NULL REFERENCES guild_cohort_members(Id) ON DELETE RESTRICT,
    PrimaryVotingQuestId TEXT NOT NULL REFERENCES guild_quests(Id) ON DELETE RESTRICT,
    ResultSequence INTEGER NOT NULL CHECK(ResultSequence>=1),
    ResultId TEXT NULL,
    OutcomeCode INTEGER NOT NULL CHECK(OutcomeCode BETWEEN 1 AND 6),
    ContentId TEXT NULL,
    ContentRevision INTEGER NULL,
    ProofDigest BLOB NULL CHECK(ProofDigest IS NULL OR (typeof(ProofDigest)='blob' AND length(ProofDigest)=32)),
    AgreementDigest BLOB NULL CHECK(AgreementDigest IS NULL OR (typeof(AgreementDigest)='blob' AND length(AgreementDigest)=32)),
    ByteLength INTEGER NULL CHECK(ByteLength IS NULL OR ByteLength BETWEEN 0 AND 65536),
    Selected INTEGER NOT NULL DEFAULT 0 CHECK(Selected IN (0,1)),
    PRIMARY KEY(CohortId,MemberId),
    UNIQUE(CohortId,ResultSequence)
);
