CREATE TABLE IF NOT EXISTS guild_signet_revocations (
    SignetId TEXT NOT NULL REFERENCES guild_signets(SignetId) ON DELETE RESTRICT,
    IdempotencyKey TEXT NOT NULL,
    CanonicalRequestDigest TEXT NOT NULL CHECK(length(CanonicalRequestDigest)=64),
    ExpectedRevision INTEGER NOT NULL CHECK(ExpectedRevision>=1),
    ReasonCode TEXT NOT NULL,
    Revision INTEGER NOT NULL CHECK(Revision>=1),
    Generation INTEGER NOT NULL CHECK(Generation>=1),
    PRIMARY KEY(SignetId,IdempotencyKey)
);
