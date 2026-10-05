CREATE TABLE IF NOT EXISTS tapestry_generations (
    GenerationId TEXT PRIMARY KEY,
    ScopeKind TEXT NOT NULL,
    ScopeId TEXT NOT NULL,
    Status TEXT NOT NULL,
    AlgorithmVersion TEXT NOT NULL,
    SettingsFingerprint TEXT NOT NULL,
    SummaryModel TEXT,
    SummaryRecipeVersion TEXT NOT NULL,
    EmbeddingDimension INTEGER NOT NULL,
    CorpusFingerprint TEXT NOT NULL,
    LayerCount INTEGER NOT NULL DEFAULT 0,
    NodeCount INTEGER NOT NULL DEFAULT 0,
    RootNodeCount INTEGER NOT NULL DEFAULT 0,
    TerminalReason TEXT,
    StartedAt TEXT NOT NULL,
    CompletedAt TEXT
);
CREATE INDEX IF NOT EXISTS idx_tapestry_generations_scope
    ON tapestry_generations(ScopeKind, ScopeId, Status);

-- Exactly one Complete generation per scope is what retrieval reads and what the publishing switch
-- assumes. The switch supersedes the previous Complete row and promotes the new one in a single
-- transaction, which keeps the invariant only for as long as every writer follows it; this index is what
-- makes a second Complete row for one scope impossible rather than merely unexpected. It covers Complete
-- rows alone, so any number of Building and Superseded generations can coexist with the current one.
CREATE UNIQUE INDEX IF NOT EXISTS ux_tapestry_generations_complete_scope
    ON tapestry_generations(ScopeKind, ScopeId) WHERE Status = 'Complete';
