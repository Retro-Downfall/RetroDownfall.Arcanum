CREATE TABLE IF NOT EXISTS guild_result_receipts (
    QuestId TEXT NOT NULL PRIMARY KEY REFERENCES guild_quests(Id) ON DELETE RESTRICT CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    ResultId TEXT NOT NULL UNIQUE CHECK(ResultId=upper(ResultId) AND length(ResultId)=36 AND substr(ResultId,9,1)='-' AND substr(ResultId,14,1)='-' AND substr(ResultId,19,1)='-' AND substr(ResultId,24,1)='-' AND length(replace(ResultId,'-',''))=32 AND replace(ResultId,'-','') NOT GLOB '*[^0-9A-F]*' AND ResultId<>'00000000-0000-0000-0000-000000000000'),
    Digest BLOB NOT NULL CHECK(typeof(Digest)='blob' AND length(Digest)=32),
    ContentId TEXT NULL CHECK(ContentId IS NULL OR (ContentId=upper(ContentId) AND length(ContentId)=36 AND substr(ContentId,9,1)='-' AND substr(ContentId,14,1)='-' AND substr(ContentId,19,1)='-' AND substr(ContentId,24,1)='-' AND length(replace(ContentId,'-',''))=32 AND replace(ContentId,'-','') NOT GLOB '*[^0-9A-F]*' AND ContentId<>'00000000-0000-0000-0000-000000000000')),
    ContentRevision INTEGER NULL CHECK(ContentRevision IS NULL OR (typeof(ContentRevision)='integer' AND ContentRevision>=1)),
    MimeType TEXT NULL CHECK(MimeType IS NULL OR length(CAST(MimeType AS BLOB)) BETWEEN 1 AND 65536),
    ByteLength INTEGER NULL CHECK(ByteLength IS NULL OR (typeof(ByteLength)='integer' AND ByteLength BETWEEN 0 AND 65536)),
    OutcomeCode INTEGER NOT NULL CHECK(OutcomeCode IN(3,4,5,6)),
    ReasonCode TEXT NULL CHECK(ReasonCode IS NULL OR length(CAST(ReasonCode AS BLOB)) BETWEEN 1 AND 65536),
    NoDispatchProven INTEGER NOT NULL CHECK(typeof(NoDispatchProven)='integer' AND NoDispatchProven IN(0,1)),
    WorkStoppedProven INTEGER NOT NULL CHECK(typeof(WorkStoppedProven)='integer' AND WorkStoppedProven IN(0,1)),
    MutationId TEXT NOT NULL CHECK(MutationId=upper(MutationId) AND length(MutationId)=36 AND substr(MutationId,9,1)='-' AND substr(MutationId,14,1)='-' AND substr(MutationId,19,1)='-' AND substr(MutationId,24,1)='-' AND length(replace(MutationId,'-',''))=32 AND replace(MutationId,'-','') NOT GLOB '*[^0-9A-F]*' AND MutationId<>'00000000-0000-0000-0000-000000000000'),
    AcceptedRevision INTEGER NOT NULL CHECK(typeof(AcceptedRevision)='integer' AND AcceptedRevision>=1),
    CreatedAtUtc TEXT NOT NULL,
    UNIQUE(QuestId,ResultId,Digest),
    CHECK((ContentId IS NULL AND ContentRevision IS NULL AND MimeType IS NULL AND ByteLength IS NULL)
       OR (ContentId IS NOT NULL AND ContentRevision IS NOT NULL AND MimeType IS NOT NULL AND ByteLength IS NOT NULL))
);
