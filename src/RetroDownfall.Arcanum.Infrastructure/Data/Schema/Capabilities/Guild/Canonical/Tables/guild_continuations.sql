CREATE TABLE IF NOT EXISTS guild_continuations (
    QuestId TEXT NOT NULL REFERENCES guild_quests(Id) ON DELETE RESTRICT CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    ContinuationId TEXT NOT NULL CHECK(ContinuationId=upper(ContinuationId) AND length(ContinuationId)=36 AND substr(ContinuationId,9,1)='-' AND substr(ContinuationId,14,1)='-' AND substr(ContinuationId,19,1)='-' AND substr(ContinuationId,24,1)='-' AND length(replace(ContinuationId,'-',''))=32 AND replace(ContinuationId,'-','') NOT GLOB '*[^0-9A-F]*' AND ContinuationId<>'00000000-0000-0000-0000-000000000000'),
    MutationId TEXT NOT NULL CHECK(MutationId=upper(MutationId) AND length(MutationId)=36 AND substr(MutationId,9,1)='-' AND substr(MutationId,14,1)='-' AND substr(MutationId,19,1)='-' AND substr(MutationId,24,1)='-' AND length(replace(MutationId,'-',''))=32 AND replace(MutationId,'-','') NOT GLOB '*[^0-9A-F]*' AND MutationId<>'00000000-0000-0000-0000-000000000000'),
    Digest BLOB NOT NULL CHECK(typeof(Digest)='blob' AND length(Digest)=32),
    ContentId TEXT NOT NULL CHECK(ContentId=upper(ContentId) AND length(ContentId)=36 AND substr(ContentId,9,1)='-' AND substr(ContentId,14,1)='-' AND substr(ContentId,19,1)='-' AND substr(ContentId,24,1)='-' AND length(replace(ContentId,'-',''))=32 AND replace(ContentId,'-','') NOT GLOB '*[^0-9A-F]*' AND ContentId<>'00000000-0000-0000-0000-000000000000'),
    AcceptedRevision INTEGER NOT NULL CHECK(typeof(AcceptedRevision)='integer' AND AcceptedRevision>=1),
    CreatedAtUtc TEXT NOT NULL,
    PRIMARY KEY(QuestId,ContinuationId),
    UNIQUE(QuestId,MutationId)
);
