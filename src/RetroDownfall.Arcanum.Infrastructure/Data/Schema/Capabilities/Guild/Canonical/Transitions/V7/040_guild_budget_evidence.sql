CREATE TABLE IF NOT EXISTS guild_budget_evidence (
    ReceiptId TEXT NOT NULL PRIMARY KEY CHECK(ReceiptId=upper(ReceiptId) AND length(ReceiptId)=36 AND substr(ReceiptId,9,1)='-' AND substr(ReceiptId,14,1)='-' AND substr(ReceiptId,19,1)='-' AND substr(ReceiptId,24,1)='-' AND length(replace(ReceiptId,'-',''))=32 AND replace(ReceiptId,'-','') NOT GLOB '*[^0-9A-F]*' AND ReceiptId<>'00000000-0000-0000-0000-000000000000'),
    EffectId TEXT NOT NULL REFERENCES guild_budget_effects(EffectId) ON DELETE RESTRICT CHECK(EffectId=upper(EffectId) AND length(EffectId)=36 AND substr(EffectId,9,1)='-' AND substr(EffectId,14,1)='-' AND substr(EffectId,19,1)='-' AND substr(EffectId,24,1)='-' AND length(replace(EffectId,'-',''))=32 AND replace(EffectId,'-','') NOT GLOB '*[^0-9A-F]*' AND EffectId<>'00000000-0000-0000-0000-000000000000'),
    Digest BLOB NOT NULL CHECK(typeof(Digest)='blob' AND length(Digest)=32),
    KnownUsd TEXT NOT NULL CHECK(typeof(KnownUsd)='text'),
    KnownTokens INTEGER NOT NULL CHECK(typeof(KnownTokens)='integer' AND KnownTokens>=0),
    UnknownUsd INTEGER NOT NULL CHECK(typeof(UnknownUsd)='integer' AND UnknownUsd IN(0,1)),
    UnknownTokens INTEGER NOT NULL CHECK(typeof(UnknownTokens)='integer' AND UnknownTokens IN(0,1)),
    Complete INTEGER NOT NULL CHECK(typeof(Complete)='integer' AND Complete IN(0,1)),
    NoDispatchProven INTEGER NOT NULL CHECK(typeof(NoDispatchProven)='integer' AND NoDispatchProven IN(0,1)),
    AppliedRevision INTEGER NOT NULL CHECK(typeof(AppliedRevision)='integer' AND AppliedRevision>=1),
    UNIQUE(EffectId,AppliedRevision)
);
