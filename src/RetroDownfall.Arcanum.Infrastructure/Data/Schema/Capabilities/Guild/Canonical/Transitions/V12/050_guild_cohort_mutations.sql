CREATE TABLE IF NOT EXISTS guild_cohort_mutations (
    IssuerQuestId TEXT NOT NULL REFERENCES guild_quests(Id) ON DELETE RESTRICT,
    ActionCode INTEGER NOT NULL,
    ParentId TEXT NOT NULL,
    IdempotencyKey TEXT NOT NULL,
    ReceiptId TEXT NOT NULL UNIQUE,
    AcceptedAtUtc TEXT NOT NULL,
    RequestDigest BLOB NOT NULL CHECK(typeof(RequestDigest)='blob' AND length(RequestDigest)=32),
    CohortId TEXT NOT NULL REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    AcceptedRevision INTEGER NOT NULL CHECK(AcceptedRevision>=1),
    PRIMARY KEY(IssuerQuestId,ActionCode,ParentId,IdempotencyKey)
);
