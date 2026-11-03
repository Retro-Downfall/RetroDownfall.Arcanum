CREATE TABLE IF NOT EXISTS "session_attachment_embeddings_quantized" (
    "ChunkId" TEXT NOT NULL PRIMARY KEY,
    "Representation" BLOB NOT NULL CHECK (typeof("Representation") = 'blob' AND length("Representation") <= 65624),
    FOREIGN KEY ("ChunkId") REFERENCES "session_attachment_embeddings" ("ChunkId") ON DELETE CASCADE ON UPDATE CASCADE
) WITHOUT ROWID;
