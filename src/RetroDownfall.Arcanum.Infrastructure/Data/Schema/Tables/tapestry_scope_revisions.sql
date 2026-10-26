CREATE TABLE IF NOT EXISTS tapestry_scope_revisions (
    ScopeKind TEXT NOT NULL,
    ScopeId TEXT NOT NULL,
    CorpusRevision INTEGER NOT NULL DEFAULT 0 CHECK (CorpusRevision >= 0),
    InvalidationRevision INTEGER NOT NULL DEFAULT 0 CHECK (InvalidationRevision >= 0 AND InvalidationRevision <= CorpusRevision),
    PRIMARY KEY (ScopeKind, ScopeId)
);
