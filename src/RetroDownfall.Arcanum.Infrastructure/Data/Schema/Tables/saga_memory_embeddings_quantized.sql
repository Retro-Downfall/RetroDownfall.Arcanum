CREATE TABLE IF NOT EXISTS "saga_memory_embeddings_quantized" (
    "MemoryId" TEXT NOT NULL PRIMARY KEY,
    "Representation" BLOB NOT NULL CHECK (typeof("Representation") = 'blob' AND length("Representation") <= 65624),
    FOREIGN KEY ("MemoryId") REFERENCES "saga_memory_embeddings" ("MemoryId") ON DELETE CASCADE ON UPDATE CASCADE
) WITHOUT ROWID;
