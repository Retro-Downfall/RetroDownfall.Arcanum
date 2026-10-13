CREATE TABLE IF NOT EXISTS long_rest_sweeps (
    OperationId TEXT NOT NULL PRIMARY KEY REFERENCES LongRunningOperations(Id) ON DELETE CASCADE,
    GenerationId TEXT NULL UNIQUE REFERENCES long_rest_discovery_generations(GenerationId) ON DELETE CASCADE,
    ScopeKindCode INTEGER NOT NULL CHECK (ScopeKindCode IN (1, 2)),
    CampaignId TEXT NULL,
    PartitionDigest BLOB NULL CHECK (PartitionDigest IS NULL OR length(PartitionDigest) = 32),
    ConfigurationJson TEXT NOT NULL CHECK (length(CAST(ConfigurationJson AS BLOB)) BETWEEN 2 AND 4096),
    PhaseCode INTEGER NOT NULL CHECK (PhaseCode BETWEEN 1 AND 9),
    Revision INTEGER NOT NULL CHECK (typeof(Revision) = 'integer' AND Revision >= 0),
    CommittedItemCursor TEXT NULL,
    DiscoveryCursor TEXT NULL,
    CommittedWorkDigest BLOB NULL CHECK (CommittedWorkDigest IS NULL OR length(CommittedWorkDigest) = 32),
    ReadmitCount INTEGER NOT NULL DEFAULT 0 CHECK (typeof(ReadmitCount) = 'integer' AND ReadmitCount >= 0),
    ParkedAt TEXT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    CHECK ((ScopeKindCode = 1 AND CampaignId IS NULL) OR (ScopeKindCode = 2 AND length(CampaignId) = 36))
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_long_rest_sweep_active_scope
ON long_rest_sweeps(ScopeKindCode, COALESCE(CampaignId, ''), COALESCE(hex(PartitionDigest), ''))
WHERE PhaseCode NOT IN (6, 7, 9);
