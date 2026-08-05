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
