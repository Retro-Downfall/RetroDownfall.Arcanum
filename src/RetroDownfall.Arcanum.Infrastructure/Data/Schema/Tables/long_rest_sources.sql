CREATE TABLE IF NOT EXISTS long_rest_sources (
    InsertionSequence INTEGER PRIMARY KEY AUTOINCREMENT,
    MemoryId TEXT NOT NULL UNIQUE REFERENCES saga_memories(Id) ON DELETE CASCADE,
    MutationEpoch INTEGER NOT NULL CHECK (typeof(MutationEpoch) = 'integer' AND MutationEpoch > 0)
);
