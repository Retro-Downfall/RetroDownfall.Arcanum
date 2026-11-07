CREATE TABLE IF NOT EXISTS guild_portal_receipts (
    InboxId TEXT NOT NULL PRIMARY KEY REFERENCES guild_inbox(Id) ON DELETE RESTRICT CHECK(InboxId=upper(InboxId) AND length(InboxId)=36 AND substr(InboxId,9,1)='-' AND substr(InboxId,14,1)='-' AND substr(InboxId,19,1)='-' AND substr(InboxId,24,1)='-' AND length(replace(InboxId,'-',''))=32 AND replace(InboxId,'-','') NOT GLOB '*[^0-9A-F]*' AND InboxId<>'00000000-0000-0000-0000-000000000000'),
    ApplicationReceiptId TEXT NOT NULL CHECK(ApplicationReceiptId=upper(ApplicationReceiptId) AND length(ApplicationReceiptId)=36 AND substr(ApplicationReceiptId,9,1)='-' AND substr(ApplicationReceiptId,14,1)='-' AND substr(ApplicationReceiptId,19,1)='-' AND substr(ApplicationReceiptId,24,1)='-' AND length(replace(ApplicationReceiptId,'-',''))=32 AND replace(ApplicationReceiptId,'-','') NOT GLOB '*[^0-9A-F]*' AND ApplicationReceiptId<>'00000000-0000-0000-0000-000000000000') UNIQUE,
    Digest BLOB NOT NULL CHECK(typeof(Digest)='blob' AND length(Digest)=32),
    EffectKindCode INTEGER NOT NULL CHECK(typeof(EffectKindCode)='integer' AND EffectKindCode BETWEEN 0 AND 3),
    CreatedAtUtc TEXT NOT NULL
);
