CREATE TABLE IF NOT EXISTS guild_events (
    RootQuestId TEXT NOT NULL CHECK(RootQuestId=upper(RootQuestId) AND length(RootQuestId)=36 AND substr(RootQuestId,9,1)='-' AND substr(RootQuestId,14,1)='-' AND substr(RootQuestId,19,1)='-' AND substr(RootQuestId,24,1)='-' AND length(replace(RootQuestId,'-',''))=32 AND replace(RootQuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND RootQuestId<>'00000000-0000-0000-0000-000000000000'),
    EventSequence INTEGER NOT NULL CHECK(typeof(EventSequence)='integer' AND EventSequence>=1),
    EventKindCode INTEGER NOT NULL CHECK(typeof(EventKindCode)='integer' AND EventKindCode BETWEEN 0 AND 12),
    ObjectId TEXT NOT NULL CHECK(ObjectId=upper(ObjectId) AND length(ObjectId)=36 AND substr(ObjectId,9,1)='-' AND substr(ObjectId,14,1)='-' AND substr(ObjectId,19,1)='-' AND substr(ObjectId,24,1)='-' AND length(replace(ObjectId,'-',''))=32 AND replace(ObjectId,'-','') NOT GLOB '*[^0-9A-F]*' AND ObjectId<>'00000000-0000-0000-0000-000000000000'),
    Revision INTEGER NOT NULL CHECK(typeof(Revision)='integer' AND Revision>=1),
    QuestId TEXT NOT NULL CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    EnvelopeId TEXT NULL CHECK(EnvelopeId IS NULL OR (EnvelopeId=upper(EnvelopeId) AND length(EnvelopeId)=36 AND substr(EnvelopeId,9,1)='-' AND substr(EnvelopeId,14,1)='-' AND substr(EnvelopeId,19,1)='-' AND substr(EnvelopeId,24,1)='-' AND length(replace(EnvelopeId,'-',''))=32 AND replace(EnvelopeId,'-','') NOT GLOB '*[^0-9A-F]*' AND EnvelopeId<>'00000000-0000-0000-0000-000000000000')),
    CreatedAtUtc TEXT NOT NULL,
    SourceKindCode INTEGER NOT NULL DEFAULT 0 CHECK(typeof(SourceKindCode)='integer' AND SourceKindCode BETWEEN 0 AND 6),
    PRIMARY KEY(RootQuestId,EventSequence),
    UNIQUE(RootQuestId,SourceKindCode,EventKindCode,ObjectId,Revision)
);
