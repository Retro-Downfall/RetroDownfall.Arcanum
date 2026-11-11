CREATE TABLE IF NOT EXISTS guild_cohort_eval_candidates (
    CohortId TEXT NOT NULL REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation>=1),
    MemberId TEXT NOT NULL REFERENCES guild_cohort_members(Id) ON DELETE RESTRICT,
    ResultSequence INTEGER NOT NULL CHECK(typeof(ResultSequence)='integer' AND ResultSequence>=1),
    OutcomeCode INTEGER NOT NULL CHECK(typeof(OutcomeCode)='integer' AND OutcomeCode BETWEEN 1 AND 6),
    BucketId INTEGER NULL CHECK(BucketId IS NULL OR (typeof(BucketId)='integer' AND BucketId>=1)),
    PRIMARY KEY(CohortId,Generation,MemberId),
    UNIQUE(CohortId,Generation,ResultSequence)
);
