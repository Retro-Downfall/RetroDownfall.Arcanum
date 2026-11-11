CREATE TABLE IF NOT EXISTS guild_cohort_settlement_state (
    CohortId TEXT NOT NULL PRIMARY KEY REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation>=1),
    Epoch INTEGER NOT NULL CHECK(typeof(Epoch)='integer' AND Epoch>=0),
    CurrentAccountId TEXT NULL,
    Complete INTEGER NOT NULL CHECK(Complete IN(0,1)),
    CHECK((Complete=0 AND CurrentAccountId IS NOT NULL) OR (Complete=1 AND CurrentAccountId IS NULL))
);
