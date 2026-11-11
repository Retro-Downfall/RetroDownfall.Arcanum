CREATE TABLE IF NOT EXISTS guild_cohort_settlement_clock (
    RootAccountId TEXT NOT NULL PRIMARY KEY,
    Revision INTEGER NOT NULL CHECK(typeof(Revision)='integer' AND Revision>=1)
);
