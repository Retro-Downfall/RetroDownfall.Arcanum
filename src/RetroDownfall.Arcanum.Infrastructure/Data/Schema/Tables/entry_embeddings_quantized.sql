CREATE TABLE IF NOT EXISTS "entry_embeddings_quantized" (
    "EntryId" TEXT NOT NULL PRIMARY KEY,
    "Representation" BLOB NOT NULL CHECK (typeof("Representation") = 'blob' AND length("Representation") <= 65624),
    FOREIGN KEY ("EntryId") REFERENCES "entry_embeddings" ("EntryId") ON DELETE CASCADE ON UPDATE CASCADE
) WITHOUT ROWID;
