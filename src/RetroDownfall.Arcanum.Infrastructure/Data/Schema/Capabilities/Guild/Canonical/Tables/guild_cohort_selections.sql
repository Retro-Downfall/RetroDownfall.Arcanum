CREATE TABLE IF NOT EXISTS guild_cohort_selections (
    CohortId TEXT NOT NULL PRIMARY KEY REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation>=1),
    KindCode INTEGER NOT NULL CHECK(typeof(KindCode)='integer' AND KindCode IN (1,2,3)),
    WinningBucket INTEGER NULL CHECK(WinningBucket IS NULL OR (typeof(WinningBucket)='integer' AND WinningBucket>=1)),
    ThroughSequence INTEGER NULL CHECK(ThroughSequence IS NULL OR (typeof(ThroughSequence)='integer' AND ThroughSequence>=1)),
    CHECK((KindCode=1 AND WinningBucket IS NULL AND ThroughSequence IS NULL)
       OR (KindCode=2 AND WinningBucket IS NULL AND ThroughSequence IS NOT NULL)
       OR (KindCode=3 AND WinningBucket IS NOT NULL AND ThroughSequence IS NOT NULL))
);
