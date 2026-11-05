CREATE TABLE IF NOT EXISTS guild_content_erasure_receipts (
    ContentId TEXT NOT NULL PRIMARY KEY CHECK((ContentId=upper(ContentId) AND length(ContentId)=36 AND substr(ContentId,9,1)='-' AND substr(ContentId,14,1)='-' AND substr(ContentId,19,1)='-' AND substr(ContentId,24,1)='-' AND length(replace(ContentId,'-',''))=32 AND replace(ContentId,'-','') NOT GLOB '*[^0-9A-F]*' AND ContentId<>'00000000-0000-0000-0000-000000000000')),
    SessionId TEXT NULL CHECK((SessionId IS NULL OR (SessionId=upper(SessionId) AND length(SessionId)=36 AND substr(SessionId,9,1)='-' AND substr(SessionId,14,1)='-' AND substr(SessionId,19,1)='-' AND substr(SessionId,24,1)='-' AND length(replace(SessionId,'-',''))=32 AND replace(SessionId,'-','') NOT GLOB '*[^0-9A-F]*' AND SessionId<>'00000000-0000-0000-0000-000000000000'))),
    CampaignId TEXT NULL CHECK((CampaignId IS NULL OR (CampaignId=upper(CampaignId) AND length(CampaignId)=36 AND substr(CampaignId,9,1)='-' AND substr(CampaignId,14,1)='-' AND substr(CampaignId,19,1)='-' AND substr(CampaignId,24,1)='-' AND length(replace(CampaignId,'-',''))=32 AND replace(CampaignId,'-','') NOT GLOB '*[^0-9A-F]*' AND CampaignId<>'00000000-0000-0000-0000-000000000000'))),
    ContentKindCode INTEGER NOT NULL CHECK(ContentKindCode BETWEEN 0 AND 8),
    SourceRecordId TEXT NOT NULL CHECK((SourceRecordId=upper(SourceRecordId) AND length(SourceRecordId)=36 AND substr(SourceRecordId,9,1)='-' AND substr(SourceRecordId,14,1)='-' AND substr(SourceRecordId,19,1)='-' AND substr(SourceRecordId,24,1)='-' AND length(replace(SourceRecordId,'-',''))=32 AND replace(SourceRecordId,'-','') NOT GLOB '*[^0-9A-F]*' AND SourceRecordId<>'00000000-0000-0000-0000-000000000000')),
    Revision INTEGER NOT NULL CHECK(Revision > 0),
    Digest BLOB NOT NULL CHECK(typeof(Digest)='blob' AND length(Digest)=32),
    MimeType TEXT NOT NULL CHECK(length(CAST(MimeType AS BLOB)) BETWEEN 1 AND 65536),
    ByteLength INTEGER NOT NULL CHECK(ByteLength BETWEEN 0 AND 65536),
    ErasedAtUtc TEXT NOT NULL
);
