-- The assistant entry is a historical identity. The Session owns its durable tool evidence.
CREATE TABLE IF NOT EXISTS session_tool_invocations (
    ReceiptId TEXT NOT NULL PRIMARY KEY CHECK (length(ReceiptId)=36 AND ReceiptId=upper(ReceiptId)),
    SessionId TEXT NOT NULL REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    AssistantEntryId TEXT NOT NULL CHECK (length(AssistantEntryId)=36 AND AssistantEntryId=upper(AssistantEntryId)),
    ClaimId TEXT NULL CHECK (ClaimId IS NULL OR (length(ClaimId)=36 AND ClaimId=upper(ClaimId))),
    RoundOrdinal INTEGER NOT NULL CHECK (typeof(RoundOrdinal)='integer' AND RoundOrdinal BETWEEN 0 AND 2147483647),
    CallOrdinal INTEGER NOT NULL CHECK (typeof(CallOrdinal)='integer' AND CallOrdinal BETWEEN 0 AND 2147483647),
    ArgumentsDigest BLOB NOT NULL CHECK (typeof(ArgumentsDigest)='blob' AND length(ArgumentsDigest)=32),
    CreatedAtUtcTicks INTEGER NOT NULL CHECK (typeof(CreatedAtUtcTicks)='integer' AND CreatedAtUtcTicks BETWEEN 0 AND 3155378975999999999),
    StateCode INTEGER NOT NULL CHECK (typeof(StateCode)='integer' AND StateCode IN (1,2,3)),
    DispatchId TEXT NULL CHECK (DispatchId IS NULL OR (length(DispatchId)=36 AND DispatchId=upper(DispatchId))),
    ResultEntryId TEXT NULL CHECK (ResultEntryId IS NULL OR (length(ResultEntryId)=36 AND ResultEntryId=upper(ResultEntryId))),
    UNIQUE (SessionId,AssistantEntryId,RoundOrdinal,CallOrdinal),
    CHECK ((StateCode=1)=(DispatchId IS NULL)),
    CHECK ((StateCode=3)=(ResultEntryId IS NOT NULL))
);
