CREATE TABLE IF NOT EXISTS guild_declared_source_claims (
    SourceIdentity TEXT NOT NULL PRIMARY KEY,
    RootKey TEXT NOT NULL REFERENCES guild_declared_roots(RootKey) ON DELETE CASCADE CHECK(length(CAST(RootKey AS BLOB))<=64),
    ExplicitAgentId TEXT NULL CHECK(ExplicitAgentId IS NULL OR length(ExplicitAgentId)=36),
    LastSeenScanId TEXT NOT NULL CHECK(length(LastSeenScanId)=36),
    ObservedValid INTEGER NOT NULL CHECK(ObservedValid IN(0,1)),
    SourceDigest TEXT NULL,
    CHECK((ObservedValid=0 AND SourceDigest IS NULL) OR (ObservedValid=1 AND SourceDigest IS NOT NULL AND length(SourceDigest)=64)),
    CHECK(length(CAST(SourceIdentity AS BLOB))+length(RootKey)+COALESCE(length(ExplicitAgentId),0)+length(LastSeenScanId)+COALESCE(length(SourceDigest),0)<=65536)
);
