CREATE TABLE IF NOT EXISTS guild_memory_attribution (
    StoreCode INTEGER NOT NULL CHECK(StoreCode IN (1,2)),
    SubjectId TEXT NOT NULL,
    ImmutableVersionId TEXT NOT NULL REFERENCES annal_versions(VersionId) ON DELETE CASCADE,
    SignetId TEXT NOT NULL REFERENCES guild_signets(SignetId) ON DELETE CASCADE,
    AgentId TEXT NOT NULL,
    RunId TEXT NOT NULL,
    QuestId TEXT NOT NULL,
    PRIMARY KEY(StoreCode,SubjectId,ImmutableVersionId)
);
