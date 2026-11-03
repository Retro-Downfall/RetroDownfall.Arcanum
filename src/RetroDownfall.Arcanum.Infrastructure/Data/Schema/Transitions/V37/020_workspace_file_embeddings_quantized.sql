CREATE TABLE IF NOT EXISTS "workspace_file_embeddings_quantized" (
    "ChunkId" TEXT NOT NULL PRIMARY KEY,
    "Representation" BLOB NOT NULL CHECK (typeof("Representation") = 'blob' AND length("Representation") <= 65624),
    FOREIGN KEY ("ChunkId") REFERENCES "workspace_file_embeddings" ("ChunkId") ON DELETE CASCADE ON UPDATE CASCADE
) WITHOUT ROWID;
