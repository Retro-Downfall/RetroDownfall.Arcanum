CREATE TABLE IF NOT EXISTS guild_roster_refreshes (
    InstallationGeneration TEXT NOT NULL,
    PrincipalId TEXT NOT NULL,
    IdempotencyKey TEXT NOT NULL,
    CanonicalRequestDigest TEXT NOT NULL CHECK(length(CanonicalRequestDigest)=64),
    NextCursor TEXT NULL REFERENCES guild_roster_cursors(Cursor) ON DELETE RESTRICT,
    ResultJson BLOB NOT NULL CHECK(typeof(ResultJson)='blob' AND length(ResultJson)<=65536),
    PRIMARY KEY(InstallationGeneration,PrincipalId,IdempotencyKey)
);
