CREATE TABLE IF NOT EXISTS long_rest_discovery_generations (
    GenerationId TEXT NOT NULL PRIMARY KEY,
    OperationId TEXT NOT NULL UNIQUE REFERENCES LongRunningOperations(Id) ON DELETE CASCADE,
    ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (1, 2)),
    CampaignId TEXT NULL,
    PartitionDigest BLOB NULL CHECK (PartitionDigest IS NULL OR length(PartitionDigest) = 32),
    InsertionHighWatermark INTEGER NOT NULL CHECK (InsertionHighWatermark >= 0),
    FenceJson TEXT NOT NULL CHECK (length(CAST(FenceJson AS BLOB)) BETWEEN 2 AND 16384),
    RecipeJson TEXT NOT NULL CHECK (length(CAST(RecipeJson AS BLOB)) BETWEEN 2 AND 16384),
    RecipeDigest BLOB NOT NULL CHECK (length(RecipeDigest) = 32),
    PhaseCode INTEGER NOT NULL CHECK (PhaseCode BETWEEN 1 AND 9),
    Revision INTEGER NOT NULL CHECK (typeof(Revision) = 'integer' AND Revision >= 0),
    CaptureCursor TEXT NULL,
    FoldLayer INTEGER NOT NULL DEFAULT 0 CHECK (FoldLayer BETWEEN 0 AND 6),
    FoldGroup INTEGER NOT NULL DEFAULT 0 CHECK (FoldGroup >= 0),
    Published INTEGER NOT NULL DEFAULT 0 CHECK (Published IN (0, 1)),
    CreatedAtUtc TEXT NOT NULL,
    CHECK ((ScopeKindCode = 1 AND CampaignId IS NULL) OR (ScopeKindCode = 2 AND length(CampaignId) = 36))
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_long_rest_generation_active_scope
ON long_rest_discovery_generations(ScopeKindCode, COALESCE(CampaignId, ''), COALESCE(hex(PartitionDigest), ''))
WHERE PhaseCode NOT IN (6, 7, 9);
