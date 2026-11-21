CREATE TABLE IF NOT EXISTS guild_remote_portal_deliveries (
    InboxId TEXT NOT NULL PRIMARY KEY CHECK(InboxId=upper(InboxId) AND length(InboxId)=36 AND substr(InboxId,9,1)='-' AND substr(InboxId,14,1)='-' AND substr(InboxId,19,1)='-' AND substr(InboxId,24,1)='-' AND length(replace(InboxId,'-',''))=32 AND replace(InboxId,'-','') NOT GLOB '*[^0-9A-F]*' AND InboxId<>'00000000-0000-0000-0000-000000000000'),
    RecipientQuestId TEXT NOT NULL CHECK(RecipientQuestId=upper(RecipientQuestId) AND length(RecipientQuestId)=36 AND substr(RecipientQuestId,9,1)='-' AND substr(RecipientQuestId,14,1)='-' AND substr(RecipientQuestId,19,1)='-' AND substr(RecipientQuestId,24,1)='-' AND length(replace(RecipientQuestId,'-',''))=32 AND replace(RecipientQuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND RecipientQuestId<>'00000000-0000-0000-0000-000000000000'),
    EnvelopeId TEXT NOT NULL CHECK(EnvelopeId=upper(EnvelopeId) AND length(EnvelopeId)=36 AND substr(EnvelopeId,9,1)='-' AND substr(EnvelopeId,14,1)='-' AND substr(EnvelopeId,19,1)='-' AND substr(EnvelopeId,24,1)='-' AND length(replace(EnvelopeId,'-',''))=32 AND replace(EnvelopeId,'-','') NOT GLOB '*[^0-9A-F]*' AND EnvelopeId<>'00000000-0000-0000-0000-000000000000'),
    TaskId TEXT NOT NULL CHECK(typeof(TaskId)='text' AND length(CAST(TaskId AS BLOB)) BETWEEN 1 AND 4096),
    ContextId TEXT NOT NULL CHECK(typeof(ContextId)='text' AND length(CAST(ContextId AS BLOB)) BETWEEN 1 AND 4096),
    TargetDigest BLOB NOT NULL CHECK(typeof(TargetDigest)='blob' AND length(TargetDigest)=32),
    TransportStateCode INTEGER NOT NULL CHECK(typeof(TransportStateCode)='integer' AND TransportStateCode IN(0,1,2)),
    WireIntentDigest BLOB NULL CHECK(WireIntentDigest IS NULL OR (typeof(WireIntentDigest)='blob' AND length(WireIntentDigest)=32)),
    PeerReceiptId TEXT NULL CHECK(PeerReceiptId IS NULL OR (PeerReceiptId=upper(PeerReceiptId) AND length(PeerReceiptId)=36 AND substr(PeerReceiptId,9,1)='-' AND substr(PeerReceiptId,14,1)='-' AND substr(PeerReceiptId,19,1)='-' AND substr(PeerReceiptId,24,1)='-' AND length(replace(PeerReceiptId,'-',''))=32 AND replace(PeerReceiptId,'-','') NOT GLOB '*[^0-9A-F]*' AND PeerReceiptId<>'00000000-0000-0000-0000-000000000000')),
    PeerRevision INTEGER NULL CHECK(PeerRevision IS NULL OR (typeof(PeerRevision)='integer' AND PeerRevision>=1)),
    PeerDeliveryStateCode INTEGER NULL CHECK(PeerDeliveryStateCode IS NULL OR (typeof(PeerDeliveryStateCode)='integer' AND PeerDeliveryStateCode IN(1,2,3))),
    PeerApplicationReceiptId TEXT NULL CHECK(PeerApplicationReceiptId IS NULL OR (PeerApplicationReceiptId=upper(PeerApplicationReceiptId) AND length(PeerApplicationReceiptId)=36 AND substr(PeerApplicationReceiptId,9,1)='-' AND substr(PeerApplicationReceiptId,14,1)='-' AND substr(PeerApplicationReceiptId,19,1)='-' AND substr(PeerApplicationReceiptId,24,1)='-' AND length(replace(PeerApplicationReceiptId,'-',''))=32 AND replace(PeerApplicationReceiptId,'-','') NOT GLOB '*[^0-9A-F]*' AND PeerApplicationReceiptId<>'00000000-0000-0000-0000-000000000000')),
    ObservationCount INTEGER NOT NULL DEFAULT 0 CHECK(typeof(ObservationCount)='integer' AND ObservationCount>=0),
    FOREIGN KEY(InboxId,RecipientQuestId,EnvelopeId) REFERENCES guild_inbox(Id,RecipientQuestId,EnvelopeId) ON DELETE RESTRICT,
    CHECK((TransportStateCode=0 AND WireIntentDigest IS NULL) OR (TransportStateCode IN(1,2) AND WireIntentDigest IS NOT NULL)),
    CHECK((TransportStateCode<2 AND PeerReceiptId IS NULL AND PeerRevision IS NULL AND PeerDeliveryStateCode IS NULL)
        OR (TransportStateCode=2 AND PeerReceiptId IS NOT NULL AND PeerRevision IS NOT NULL AND PeerDeliveryStateCode IS NOT NULL)),
    CHECK((PeerDeliveryStateCode IS 2 AND PeerApplicationReceiptId IS NOT NULL)
        OR (PeerDeliveryStateCode IS NOT 2 AND PeerApplicationReceiptId IS NULL))
);
