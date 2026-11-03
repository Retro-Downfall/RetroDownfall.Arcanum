CREATE TABLE IF NOT EXISTS "tapestry_node_embeddings_quantized" (
    "NodeId" TEXT NOT NULL PRIMARY KEY,
    "Representation" BLOB NOT NULL CHECK (typeof("Representation") = 'blob' AND length("Representation") <= 65624),
    FOREIGN KEY ("NodeId") REFERENCES "tapestry_node_embeddings" ("NodeId") ON DELETE CASCADE ON UPDATE CASCADE
) WITHOUT ROWID;
