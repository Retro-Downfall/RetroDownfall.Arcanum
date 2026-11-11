CREATE TABLE IF NOT EXISTS guild_work_mutations (
    PrincipalKindCode INTEGER NOT NULL CHECK(PrincipalKindCode IN (1,2)),
    PrincipalId TEXT NOT NULL,
    ActionCode INTEGER NOT NULL CHECK(ActionCode BETWEEN 1 AND 5),
    ParentId TEXT NOT NULL,
    IdempotencyKey TEXT NOT NULL,
    RequestDigest BLOB NOT NULL CHECK(typeof(RequestDigest)='blob' AND length(RequestDigest)=32),
    ResultId TEXT NOT NULL,
    ReceiptId TEXT NOT NULL,
    AcceptedRevision INTEGER NOT NULL CHECK(typeof(AcceptedRevision)='integer' AND AcceptedRevision>=1),
    AcceptedAtUtc TEXT NOT NULL,
    PRIMARY KEY(PrincipalKindCode,PrincipalId,ActionCode,ParentId,IdempotencyKey),
    CHECK(typeof(PrincipalId)='text' AND PrincipalId=upper(PrincipalId) AND length(PrincipalId)=36 AND substr(PrincipalId,9,1)='-' AND substr(PrincipalId,14,1)='-' AND substr(PrincipalId,19,1)='-' AND substr(PrincipalId,24,1)='-' AND length(replace(PrincipalId,'-',''))=32 AND replace(PrincipalId,'-','') NOT GLOB '*[^0-9A-F]*' AND PrincipalId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(ParentId)='text' AND ParentId=upper(ParentId) AND length(ParentId)=36 AND substr(ParentId,9,1)='-' AND substr(ParentId,14,1)='-' AND substr(ParentId,19,1)='-' AND substr(ParentId,24,1)='-' AND length(replace(ParentId,'-',''))=32 AND replace(ParentId,'-','') NOT GLOB '*[^0-9A-F]*' AND ParentId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(IdempotencyKey)='text' AND IdempotencyKey=upper(IdempotencyKey) AND length(IdempotencyKey)=36 AND substr(IdempotencyKey,9,1)='-' AND substr(IdempotencyKey,14,1)='-' AND substr(IdempotencyKey,19,1)='-' AND substr(IdempotencyKey,24,1)='-' AND length(replace(IdempotencyKey,'-',''))=32 AND replace(IdempotencyKey,'-','') NOT GLOB '*[^0-9A-F]*' AND IdempotencyKey<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(ResultId)='text' AND ResultId=upper(ResultId) AND length(ResultId)=36 AND substr(ResultId,9,1)='-' AND substr(ResultId,14,1)='-' AND substr(ResultId,19,1)='-' AND substr(ResultId,24,1)='-' AND length(replace(ResultId,'-',''))=32 AND replace(ResultId,'-','') NOT GLOB '*[^0-9A-F]*' AND ResultId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(ReceiptId)='text' AND ReceiptId=upper(ReceiptId) AND length(ReceiptId)=36 AND substr(ReceiptId,9,1)='-' AND substr(ReceiptId,14,1)='-' AND substr(ReceiptId,19,1)='-' AND substr(ReceiptId,24,1)='-' AND length(replace(ReceiptId,'-',''))=32 AND replace(ReceiptId,'-','') NOT GLOB '*[^0-9A-F]*' AND ReceiptId<>'00000000-0000-0000-0000-000000000000'),
    CHECK(typeof(AcceptedAtUtc)='text' AND length(CAST(AcceptedAtUtc AS BLOB))=28)
);
