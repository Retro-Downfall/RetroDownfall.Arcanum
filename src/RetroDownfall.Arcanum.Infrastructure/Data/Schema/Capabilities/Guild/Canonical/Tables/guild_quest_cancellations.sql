CREATE TABLE IF NOT EXISTS guild_quest_cancellations (
    QuestId TEXT NOT NULL CHECK(typeof(QuestId)='text' AND QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    IdempotencyKey TEXT NOT NULL CHECK(typeof(IdempotencyKey)='text' AND IdempotencyKey=upper(IdempotencyKey) AND length(IdempotencyKey)=36 AND substr(IdempotencyKey,9,1)='-' AND substr(IdempotencyKey,14,1)='-' AND substr(IdempotencyKey,19,1)='-' AND substr(IdempotencyKey,24,1)='-' AND length(replace(IdempotencyKey,'-',''))=32 AND replace(IdempotencyKey,'-','') NOT GLOB '*[^0-9A-F]*' AND IdempotencyKey<>'00000000-0000-0000-0000-000000000000'),
    CanonicalRequestDigest BLOB NOT NULL CHECK(typeof(CanonicalRequestDigest)='blob' AND length(CanonicalRequestDigest)=32),
    ExpectedRevision INTEGER NOT NULL CHECK(typeof(ExpectedRevision)='integer' AND ExpectedRevision>=1),
    ReasonCode INTEGER NOT NULL CHECK(typeof(ReasonCode)='integer' AND ReasonCode IN(0,1)),
    AcceptedRevision INTEGER NOT NULL CHECK(typeof(AcceptedRevision)='integer' AND AcceptedRevision>=ExpectedRevision),
    AcceptedAtUtc TEXT NOT NULL,
    PRIMARY KEY(QuestId,IdempotencyKey)
);
