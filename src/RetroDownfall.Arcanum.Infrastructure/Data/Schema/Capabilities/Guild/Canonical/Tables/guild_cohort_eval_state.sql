CREATE TABLE IF NOT EXISTS guild_cohort_eval_state (
    CohortId TEXT NOT NULL PRIMARY KEY REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation>=1),
    MembershipVersion INTEGER NOT NULL CHECK(typeof(MembershipVersion)='integer' AND MembershipVersion>=1),
    ResultSequence INTEGER NOT NULL CHECK(typeof(ResultSequence)='integer' AND ResultSequence>=0),
    ContentEpoch INTEGER NOT NULL CHECK(typeof(ContentEpoch)='integer' AND ContentEpoch>=0),
    SensitivityEpoch INTEGER NOT NULL CHECK(typeof(SensitivityEpoch)='integer' AND SensitivityEpoch>=0),
    OwnerEpoch INTEGER NOT NULL CHECK(typeof(OwnerEpoch)='integer' AND OwnerEpoch>=0),
    VisibilityEpoch TEXT NOT NULL CHECK(length(CAST(VisibilityEpoch AS BLOB))<=4096),
    LastResultSequence INTEGER NOT NULL DEFAULT 0 CHECK(typeof(LastResultSequence)='integer' AND LastResultSequence>=0),
    ProcessedCount INTEGER NOT NULL DEFAULT 0 CHECK(typeof(ProcessedCount)='integer' AND ProcessedCount>=0),
    InvalidCount INTEGER NOT NULL DEFAULT 0 CHECK(typeof(InvalidCount)='integer' AND InvalidCount>=0),
    MaxBucketCount INTEGER NOT NULL DEFAULT 0 CHECK(typeof(MaxBucketCount)='integer' AND MaxBucketCount>=0),
    PendingResultSequence INTEGER NULL CHECK(PendingResultSequence IS NULL OR (typeof(PendingResultSequence)='integer' AND PendingResultSequence>=1)),
    CollisionAfterBucket INTEGER NOT NULL DEFAULT 0 CHECK(typeof(CollisionAfterBucket)='integer' AND CollisionAfterBucket>=0)
);
