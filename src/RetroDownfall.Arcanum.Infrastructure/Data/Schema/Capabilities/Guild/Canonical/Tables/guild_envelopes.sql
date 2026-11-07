CREATE TABLE IF NOT EXISTS guild_envelopes (
    Id TEXT NOT NULL PRIMARY KEY CHECK(Id=upper(Id) AND length(Id)=36 AND substr(Id,9,1)='-' AND substr(Id,14,1)='-' AND substr(Id,19,1)='-' AND substr(Id,24,1)='-' AND length(replace(Id,'-',''))=32 AND replace(Id,'-','') NOT GLOB '*[^0-9A-F]*' AND Id<>'00000000-0000-0000-0000-000000000000'),
    StreamId TEXT NOT NULL REFERENCES guild_portal_streams(StreamId) ON DELETE RESTRICT CHECK(StreamId=upper(StreamId) AND length(StreamId)=36 AND substr(StreamId,9,1)='-' AND substr(StreamId,14,1)='-' AND substr(StreamId,19,1)='-' AND substr(StreamId,24,1)='-' AND length(replace(StreamId,'-',''))=32 AND replace(StreamId,'-','') NOT GLOB '*[^0-9A-F]*' AND StreamId<>'00000000-0000-0000-0000-000000000000'),
    ProducerRequestId TEXT NOT NULL CHECK((typeof(ProducerRequestId)='text' AND length(CAST(ProducerRequestId AS BLOB)) BETWEEN 1 AND 4096)),
    IntentDigest BLOB NOT NULL CHECK(typeof(IntentDigest)='blob' AND length(IntentDigest)=32),
    QuestId TEXT NOT NULL CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    RootQuestId TEXT NOT NULL CHECK(RootQuestId=upper(RootQuestId) AND length(RootQuestId)=36 AND substr(RootQuestId,9,1)='-' AND substr(RootQuestId,14,1)='-' AND substr(RootQuestId,19,1)='-' AND substr(RootQuestId,24,1)='-' AND length(replace(RootQuestId,'-',''))=32 AND replace(RootQuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND RootQuestId<>'00000000-0000-0000-0000-000000000000'),
    SessionId TEXT NULL CHECK(SessionId IS NULL OR (SessionId=upper(SessionId) AND length(SessionId)=36 AND substr(SessionId,9,1)='-' AND substr(SessionId,14,1)='-' AND substr(SessionId,19,1)='-' AND substr(SessionId,24,1)='-' AND length(replace(SessionId,'-',''))=32 AND replace(SessionId,'-','') NOT GLOB '*[^0-9A-F]*' AND SessionId<>'00000000-0000-0000-0000-000000000000')),
    CampaignId TEXT NULL CHECK(CampaignId IS NULL OR (CampaignId=upper(CampaignId) AND length(CampaignId)=36 AND substr(CampaignId,9,1)='-' AND substr(CampaignId,14,1)='-' AND substr(CampaignId,19,1)='-' AND substr(CampaignId,24,1)='-' AND length(replace(CampaignId,'-',''))=32 AND replace(CampaignId,'-','') NOT GLOB '*[^0-9A-F]*' AND CampaignId<>'00000000-0000-0000-0000-000000000000')),
    KindCode INTEGER NOT NULL CHECK(typeof(KindCode)='integer' AND KindCode BETWEEN 0 AND 5),
    ContentId TEXT NOT NULL CHECK(ContentId=upper(ContentId) AND length(ContentId)=36 AND substr(ContentId,9,1)='-' AND substr(ContentId,14,1)='-' AND substr(ContentId,19,1)='-' AND substr(ContentId,24,1)='-' AND length(replace(ContentId,'-',''))=32 AND replace(ContentId,'-','') NOT GLOB '*[^0-9A-F]*' AND ContentId<>'00000000-0000-0000-0000-000000000000'),
    SenderSequence INTEGER NOT NULL CHECK(typeof(SenderSequence)='integer' AND SenderSequence>=1),
    MetadataJson TEXT NOT NULL CHECK((typeof(MetadataJson)='text' AND length(CAST(MetadataJson AS BLOB)) BETWEEN 1 AND 16384)),
    CreatedAtUtc TEXT NOT NULL,
    UNIQUE(StreamId,ProducerRequestId),
    UNIQUE(StreamId,SenderSequence),
    UNIQUE(Id,StreamId,SenderSequence)
);
