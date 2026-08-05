CREATE TABLE IF NOT EXISTS saga_memories (
    Id TEXT PRIMARY KEY,
    Content TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    SessionId TEXT,
    Tags TEXT,
    Source TEXT
);

CREATE INDEX IF NOT EXISTS idx_saga_memories_session ON saga_memories(SessionId);

CREATE INDEX IF NOT EXISTS idx_saga_memories_created ON saga_memories(CreatedAt);
