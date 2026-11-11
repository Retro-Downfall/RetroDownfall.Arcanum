CREATE TABLE IF NOT EXISTS guild_cohort_award_authority_scan (
    CohortId TEXT NOT NULL PRIMARY KEY REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    PhaseCode INTEGER NOT NULL DEFAULT 1 CHECK(typeof(PhaseCode)='integer' AND PhaseCode IN(1,2)),
    SnapshotSequence INTEGER NULL CHECK(SnapshotSequence IS NULL OR typeof(SnapshotSequence)='integer' AND SnapshotSequence>=0),
    LastMemberSequence INTEGER NOT NULL DEFAULT 0 CHECK(typeof(LastMemberSequence)='integer' AND LastMemberSequence>=0),
    LastOfferSequence INTEGER NOT NULL DEFAULT 0 CHECK(typeof(LastOfferSequence)='integer' AND LastOfferSequence>=0)
);
