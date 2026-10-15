CREATE TABLE IF NOT EXISTS long_rest_sources (
    InsertionSequence INTEGER PRIMARY KEY AUTOINCREMENT,
    MemoryId TEXT NOT NULL UNIQUE REFERENCES saga_memories(Id) ON DELETE CASCADE,
    MutationEpoch INTEGER NOT NULL CHECK (typeof(MutationEpoch) = 'integer' AND MutationEpoch > 0)
);

-- A Saga label is keyed by the memory's identity in whatever spelling it was stored, so a label write finds the source it taints by
-- lower(replace(MemoryId, '-', '')) on both sides, and a mis-spelled label must still bump the epoch of the source it names. SQLite answers
-- a function-wrapped column only from an index on that exact expression, so without this one every label insert and delete read every
-- tracked source. The expression has to stay character for character the shape the predicate has, because that is how SQLite decides the
-- index applies.
CREATE INDEX IF NOT EXISTS idx_long_rest_sources_memory_norm
    ON long_rest_sources(lower(replace(MemoryId, '-', '')));
