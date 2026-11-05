CREATE TABLE IF NOT EXISTS guild_roster_cursors (
    Cursor TEXT NOT NULL PRIMARY KEY CHECK(length(Cursor)=64),
    InstallationGeneration TEXT NOT NULL,
    PrincipalId TEXT NOT NULL,
    BindingDigest TEXT NOT NULL CHECK(length(BindingDigest)=64),
    Kind TEXT NOT NULL CHECK(Kind IN('list','refresh')),
    Stage INTEGER NOT NULL CHECK(Stage BETWEEN 0 AND 4),
    AfterKey TEXT NULL,
    SnapshotSequence INTEGER NULL CHECK(SnapshotSequence IS NULL OR SnapshotSequence>=0),
    GlobalDiscoveryId TEXT NULL,
    WorkspaceDiscoveryId TEXT NULL,
    CHECK(AfterKey IS NULL OR length(CAST(AfterKey AS BLOB))<=1024)
);
