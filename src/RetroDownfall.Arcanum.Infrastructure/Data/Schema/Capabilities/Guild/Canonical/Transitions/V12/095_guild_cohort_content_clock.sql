CREATE TABLE IF NOT EXISTS guild_cohort_content_clock (
    Id INTEGER NOT NULL PRIMARY KEY CHECK(Id=1),
    Revision INTEGER NOT NULL CHECK(typeof(Revision)='integer' AND Revision>=1)
);
