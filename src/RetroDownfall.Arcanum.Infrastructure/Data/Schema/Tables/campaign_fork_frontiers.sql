-- SourceSessionId is historical proof, retained after the parent Session is removed.
CREATE TABLE IF NOT EXISTS campaign_fork_frontiers (
    SessionId TEXT NOT NULL PRIMARY KEY REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    SourceSessionId TEXT NOT NULL,
    InheritedThroughSequence INTEGER NOT NULL CHECK (InheritedThroughSequence >= 0),
    ProofKindCode INTEGER NOT NULL CHECK (ProofKindCode IN (1, 2)),
    CreatedAtUtc TEXT NOT NULL
);
