CREATE TABLE IF NOT EXISTS guild_local_wait_checkpoints (
    QuestId TEXT NOT NULL CHECK(typeof(QuestId)='text' AND QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    QuestionId TEXT NOT NULL CHECK(typeof(QuestionId)='text' AND QuestionId=upper(QuestionId) AND length(QuestionId)=36 AND substr(QuestionId,9,1)='-' AND substr(QuestionId,14,1)='-' AND substr(QuestionId,19,1)='-' AND substr(QuestionId,24,1)='-' AND length(replace(QuestionId,'-',''))=32 AND replace(QuestionId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestionId<>'00000000-0000-0000-0000-000000000000'),
    ContinuationId TEXT NOT NULL CHECK(typeof(ContinuationId)='text' AND ContinuationId=upper(ContinuationId) AND length(ContinuationId)=36 AND substr(ContinuationId,9,1)='-' AND substr(ContinuationId,14,1)='-' AND substr(ContinuationId,19,1)='-' AND substr(ContinuationId,24,1)='-' AND length(replace(ContinuationId,'-',''))=32 AND replace(ContinuationId,'-','') NOT GLOB '*[^0-9A-F]*' AND ContinuationId<>'00000000-0000-0000-0000-000000000000'),
    DispatchId TEXT NOT NULL CHECK(typeof(DispatchId)='text' AND DispatchId=upper(DispatchId) AND length(DispatchId)=36 AND substr(DispatchId,9,1)='-' AND substr(DispatchId,14,1)='-' AND substr(DispatchId,19,1)='-' AND substr(DispatchId,24,1)='-' AND length(replace(DispatchId,'-',''))=32 AND replace(DispatchId,'-','') NOT GLOB '*[^0-9A-F]*' AND DispatchId<>'00000000-0000-0000-0000-000000000000'),
    ContentId TEXT NOT NULL CHECK(typeof(ContentId)='text' AND ContentId=upper(ContentId) AND length(ContentId)=36 AND substr(ContentId,9,1)='-' AND substr(ContentId,14,1)='-' AND substr(ContentId,19,1)='-' AND substr(ContentId,24,1)='-' AND length(replace(ContentId,'-',''))=32 AND replace(ContentId,'-','') NOT GLOB '*[^0-9A-F]*' AND ContentId<>'00000000-0000-0000-0000-000000000000'),
    Digest BLOB NOT NULL CHECK(typeof(Digest)='blob' AND length(Digest)=32),
    ByteLength INTEGER NOT NULL CHECK(typeof(ByteLength)='integer' AND ByteLength BETWEEN 1 AND 65536),
    PRIMARY KEY(QuestId,QuestionId,ContinuationId),
    UNIQUE(QuestId,ContinuationId),
    UNIQUE(ContentId)
);
