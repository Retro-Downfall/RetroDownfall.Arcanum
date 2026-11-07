CREATE TABLE IF NOT EXISTS guild_portal_streams (
    StreamId TEXT NOT NULL PRIMARY KEY CHECK(StreamId=upper(StreamId) AND length(StreamId)=36 AND substr(StreamId,9,1)='-' AND substr(StreamId,14,1)='-' AND substr(StreamId,19,1)='-' AND substr(StreamId,24,1)='-' AND length(replace(StreamId,'-',''))=32 AND replace(StreamId,'-','') NOT GLOB '*[^0-9A-F]*' AND StreamId<>'00000000-0000-0000-0000-000000000000'),
    QuestId TEXT NOT NULL CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    ProducerKindCode INTEGER NOT NULL CHECK(typeof(ProducerKindCode)='integer' AND ProducerKindCode BETWEEN 0 AND 2),
    ProducerKey TEXT NOT NULL CHECK((typeof(ProducerKey)='text' AND length(CAST(ProducerKey AS BLOB)) BETWEEN 1 AND 4096)),
    LastSequence INTEGER NOT NULL CHECK(typeof(LastSequence)='integer' AND LastSequence>=0),
    UNIQUE(QuestId,ProducerKindCode,ProducerKey)
);
