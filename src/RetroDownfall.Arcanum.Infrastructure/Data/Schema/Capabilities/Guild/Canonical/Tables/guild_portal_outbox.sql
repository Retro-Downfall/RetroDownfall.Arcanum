CREATE TABLE IF NOT EXISTS guild_portal_outbox (
    SourceKindCode INTEGER NOT NULL CHECK(typeof(SourceKindCode)='integer' AND SourceKindCode BETWEEN 0 AND 2),
    ReceiptId TEXT NOT NULL CHECK(ReceiptId=upper(ReceiptId) AND length(ReceiptId)=36 AND substr(ReceiptId,9,1)='-' AND substr(ReceiptId,14,1)='-' AND substr(ReceiptId,19,1)='-' AND substr(ReceiptId,24,1)='-' AND length(replace(ReceiptId,'-',''))=32 AND replace(ReceiptId,'-','') NOT GLOB '*[^0-9A-F]*' AND ReceiptId<>'00000000-0000-0000-0000-000000000000'),
    QuestId TEXT NOT NULL CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    EnvelopeId TEXT NULL CHECK(EnvelopeId IS NULL OR (EnvelopeId=upper(EnvelopeId) AND length(EnvelopeId)=36 AND substr(EnvelopeId,9,1)='-' AND substr(EnvelopeId,14,1)='-' AND substr(EnvelopeId,19,1)='-' AND substr(EnvelopeId,24,1)='-' AND length(replace(EnvelopeId,'-',''))=32 AND replace(EnvelopeId,'-','') NOT GLOB '*[^0-9A-F]*' AND EnvelopeId<>'00000000-0000-0000-0000-000000000000')),
    CreatedAtUtc TEXT NOT NULL,
    DeliveryStateCode INTEGER NOT NULL DEFAULT 1 CHECK(typeof(DeliveryStateCode)='integer' AND DeliveryStateCode IN(1,2,3)),
    PRIMARY KEY(SourceKindCode,ReceiptId),
    CHECK((DeliveryStateCode=2 AND EnvelopeId IS NOT NULL) OR (DeliveryStateCode IN(1,3) AND EnvelopeId IS NULL))
);
