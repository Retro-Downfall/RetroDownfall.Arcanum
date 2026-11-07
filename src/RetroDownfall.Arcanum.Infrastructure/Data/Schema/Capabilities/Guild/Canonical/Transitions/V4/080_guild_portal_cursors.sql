CREATE TABLE IF NOT EXISTS guild_portal_cursors (
    Id TEXT NOT NULL PRIMARY KEY CHECK(Id=upper(Id) AND length(Id)=36 AND substr(Id,9,1)='-' AND substr(Id,14,1)='-' AND substr(Id,19,1)='-' AND substr(Id,24,1)='-' AND length(replace(Id,'-',''))=32 AND replace(Id,'-','') NOT GLOB '*[^0-9A-F]*' AND Id<>'00000000-0000-0000-0000-000000000000'),
    InstallationGeneration TEXT NOT NULL CHECK(InstallationGeneration=upper(InstallationGeneration) AND length(InstallationGeneration)=36 AND substr(InstallationGeneration,9,1)='-' AND substr(InstallationGeneration,14,1)='-' AND substr(InstallationGeneration,19,1)='-' AND substr(InstallationGeneration,24,1)='-' AND length(replace(InstallationGeneration,'-',''))=32 AND replace(InstallationGeneration,'-','') NOT GLOB '*[^0-9A-F]*' AND InstallationGeneration<>'00000000-0000-0000-0000-000000000000'),
    CallerKey TEXT NOT NULL CHECK((typeof(CallerKey)='text' AND length(CAST(CallerKey AS BLOB)) BETWEEN 1 AND 256)),
    RootQuestId TEXT NOT NULL CHECK(RootQuestId=upper(RootQuestId) AND length(RootQuestId)=36 AND substr(RootQuestId,9,1)='-' AND substr(RootQuestId,14,1)='-' AND substr(RootQuestId,19,1)='-' AND substr(RootQuestId,24,1)='-' AND length(replace(RootQuestId,'-',''))=32 AND replace(RootQuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND RootQuestId<>'00000000-0000-0000-0000-000000000000'),
    ScopeKey TEXT NOT NULL CHECK((typeof(ScopeKey)='text' AND length(CAST(ScopeKey AS BLOB)) BETWEEN 1 AND 512)),
    AfterSequence INTEGER NOT NULL CHECK(typeof(AfterSequence)='integer' AND AfterSequence>=0),
    SnapshotSequence INTEGER NOT NULL CHECK(typeof(SnapshotSequence)='integer' AND SnapshotSequence>=0),
    CreatedAtUtc TEXT NOT NULL,
    AuthorityEpoch TEXT NOT NULL CHECK(AuthorityEpoch=upper(AuthorityEpoch) AND length(AuthorityEpoch)=36 AND substr(AuthorityEpoch,9,1)='-' AND substr(AuthorityEpoch,14,1)='-' AND substr(AuthorityEpoch,19,1)='-' AND substr(AuthorityEpoch,24,1)='-' AND length(replace(AuthorityEpoch,'-',''))=32 AND replace(AuthorityEpoch,'-','') NOT GLOB '*[^0-9A-F]*' AND AuthorityEpoch<>'00000000-0000-0000-0000-000000000000'),
    VisibilityKey TEXT NOT NULL CHECK((typeof(VisibilityKey)='text' AND length(CAST(VisibilityKey AS BLOB)) BETWEEN 1 AND 256)),
    CHECK(AfterSequence<=SnapshotSequence)
);
