CREATE TABLE IF NOT EXISTS guild_spend_receipts (
    ReceiptId TEXT NOT NULL PRIMARY KEY CHECK(typeof(ReceiptId)='text' AND ReceiptId=upper(ReceiptId) AND length(ReceiptId)=36 AND substr(ReceiptId,9,1)='-' AND substr(ReceiptId,14,1)='-' AND substr(ReceiptId,19,1)='-' AND substr(ReceiptId,24,1)='-' AND length(replace(ReceiptId,'-',''))=32 AND replace(ReceiptId,'-','') NOT GLOB '*[^0-9A-F]*' AND ReceiptId<>'00000000-0000-0000-0000-000000000000'),
    EffectId TEXT NOT NULL REFERENCES guild_budget_effects(EffectId) ON DELETE RESTRICT CHECK(typeof(EffectId)='text' AND EffectId=upper(EffectId) AND length(EffectId)=36 AND substr(EffectId,9,1)='-' AND substr(EffectId,14,1)='-' AND substr(EffectId,19,1)='-' AND substr(EffectId,24,1)='-' AND length(replace(EffectId,'-',''))=32 AND replace(EffectId,'-','') NOT GLOB '*[^0-9A-F]*' AND EffectId<>'00000000-0000-0000-0000-000000000000'),
    EvidenceReceiptId TEXT NOT NULL REFERENCES guild_budget_evidence(ReceiptId) ON DELETE RESTRICT CHECK(typeof(EvidenceReceiptId)='text' AND EvidenceReceiptId=upper(EvidenceReceiptId) AND length(EvidenceReceiptId)=36 AND substr(EvidenceReceiptId,9,1)='-' AND substr(EvidenceReceiptId,14,1)='-' AND substr(EvidenceReceiptId,19,1)='-' AND substr(EvidenceReceiptId,24,1)='-' AND length(replace(EvidenceReceiptId,'-',''))=32 AND replace(EvidenceReceiptId,'-','') NOT GLOB '*[^0-9A-F]*' AND EvidenceReceiptId<>'00000000-0000-0000-0000-000000000000'),
    EvidenceRevision INTEGER NOT NULL CHECK(typeof(EvidenceRevision)='integer' AND EvidenceRevision>=1),
    OriginCode INTEGER NOT NULL CHECK(typeof(OriginCode)='integer' AND OriginCode BETWEEN 0 AND 3),
    RequestIdentity TEXT NOT NULL CHECK(typeof(RequestIdentity)='text' AND length(RequestIdentity)>0),
    RequestDigest BLOB NOT NULL CHECK(typeof(RequestDigest)='blob' AND length(RequestDigest)=32),
    OriginalResult BLOB NOT NULL CHECK(typeof(OriginalResult)='blob'),
    OriginalEventSequence INTEGER NOT NULL CHECK(typeof(OriginalEventSequence)='integer' AND OriginalEventSequence>=1),
    AcceptedAtUtc TEXT NOT NULL CHECK(typeof(AcceptedAtUtc)='text'),
    UNIQUE(OriginCode,RequestIdentity),
    UNIQUE(EffectId,EvidenceRevision)
);
