-- Immutable registration and root-last erasure lookup. Keys are code-generated opaque coordinates.
CREATE TABLE IF NOT EXISTS memory_evaluations (
    EvaluationId TEXT NOT NULL PRIMARY KEY CHECK(length(EvaluationId) = 32 AND EvaluationId NOT GLOB '*[^0-9a-f]*'),
    OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId) = 32 AND OperationId NOT GLOB '*[^0-9a-f]*'),
    DatasetGeneration TEXT NOT NULL CHECK(length(DatasetGeneration) = 32 AND DatasetGeneration NOT GLOB '*[^0-9a-f]*'),
    ScopeCode INTEGER NOT NULL CHECK(ScopeCode IN (1,2)),
    CampaignId TEXT NULL CHECK(CampaignId IS NULL OR (length(CampaignId) = 32 AND CampaignId NOT GLOB '*[^0-9a-f]*')),
    CreatedAtUtc TEXT NOT NULL CHECK(length(CreatedAtUtc) = 28 AND substr(CreatedAtUtc,28,1) = 'Z'),
    SourceAsOfUtc TEXT NOT NULL CHECK(length(SourceAsOfUtc) = 28 AND substr(SourceAsOfUtc,28,1) = 'Z' AND SourceAsOfUtc <= CreatedAtUtc),
    SourceCount INTEGER NOT NULL CHECK(SourceCount BETWEEN 1 AND 128),
    RegistrationJson TEXT NOT NULL CHECK(json_valid(RegistrationJson) AND length(CAST(RegistrationJson AS BLOB)) BETWEEN 1 AND 65536),
    ErasureKeysJson TEXT NOT NULL CHECK(json_valid(ErasureKeysJson) AND json_type(ErasureKeysJson) = 'array' AND length(CAST(ErasureKeysJson AS BLOB)) <= 65536),
    PreparedBytes INTEGER NOT NULL CHECK(PreparedBytes BETWEEN 1 AND 57344),
    CHECK((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_memory_evaluations_campaign ON memory_evaluations(CampaignId);
