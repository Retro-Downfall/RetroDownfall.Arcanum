CREATE TABLE IF NOT EXISTS guild_declared_roots (
    RootKey TEXT NOT NULL PRIMARY KEY CHECK(length(CAST(RootKey AS BLOB))<=64),
    SourceKindCode INTEGER NOT NULL CHECK(SourceKindCode IN(0,1)),
    WorkspaceId TEXT NULL CHECK(WorkspaceId IS NULL OR length(WorkspaceId)=36),
    InstallationGeneration TEXT NOT NULL CHECK(length(InstallationGeneration)=36),
    PathDigest TEXT NOT NULL CHECK(length(PathDigest)=64),
    ScanId TEXT NOT NULL CHECK(length(ScanId)=36),
    StateCode INTEGER NOT NULL CHECK(StateCode IN(0,1,2)),
    CHECK((SourceKindCode=0 AND WorkspaceId IS NULL) OR (SourceKindCode=1 AND WorkspaceId IS NOT NULL))
);
