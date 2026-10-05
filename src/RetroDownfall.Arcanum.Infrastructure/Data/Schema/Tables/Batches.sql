CREATE TABLE IF NOT EXISTS "Batches" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Batches" PRIMARY KEY,
    "InputFileId" TEXT NOT NULL,
    "Endpoint" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CompletedAt" TEXT NULL,
    "OutputFileId" TEXT NULL,
    "ErrorFileId" TEXT NULL,
    "TotalRequestCount" INTEGER NOT NULL DEFAULT 0,
    "CompletedRequestCount" INTEGER NOT NULL DEFAULT 0,
    "FailedRequestCount" INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT "CK_Batches_RequestCounts" CHECK (
        "TotalRequestCount" >= 0
        AND "CompletedRequestCount" >= 0
        AND "FailedRequestCount" >= 0
        AND "CompletedRequestCount" + "FailedRequestCount" <= "TotalRequestCount"
    )
);

CREATE INDEX IF NOT EXISTS "IX_Batches_Status" ON "Batches" ("Status");

CREATE INDEX IF NOT EXISTS "IX_Batches_CreatedAt" ON "Batches" ("CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_Batches_CreatedAt_Id" ON "Batches" ("CreatedAt" DESC, "Id" DESC);

-- Each of the three file roles a batch names is looked up by the file, from the file's side: deleting an
-- uploaded file, and the retention sweep's reference check, ask whether any batch still names it. Without
-- an index per column each of those asks scans every batch the installation ever held. The value stored
-- here is the canonical uppercase dashed identity, so the lookup is an exact equality and these ordinary
-- column indexes answer it.
CREATE INDEX IF NOT EXISTS "IX_Batches_InputFileId" ON "Batches" ("InputFileId");

CREATE INDEX IF NOT EXISTS "IX_Batches_OutputFileId" ON "Batches" ("OutputFileId");

CREATE INDEX IF NOT EXISTS "IX_Batches_ErrorFileId" ON "Batches" ("ErrorFileId");

-- The retention sweep names the same three roles normalized - lower(replace(col, '-', '')) - because it has to
-- stay correct for whatever spelling a file identity was ever written in, and it asks the file's question from
-- two sides: whether a batch still names an aged upload, and which batches name it. SQLite cannot answer a
-- function-wrapped column from the plain indexes above, so without an index on each wrapped expression every
-- one of those asks is a scan of every batch the installation ever held, once per candidate file. Each
-- expression here has to stay character for character the shape the predicate has, because that is how SQLite
-- decides the index applies.
CREATE INDEX IF NOT EXISTS "IX_Batches_InputFileId_Norm"
  ON "Batches" (lower(replace("InputFileId", '-', '')));

CREATE INDEX IF NOT EXISTS "IX_Batches_OutputFileId_Norm"
  ON "Batches" (lower(replace("OutputFileId", '-', '')));

CREATE INDEX IF NOT EXISTS "IX_Batches_ErrorFileId_Norm"
  ON "Batches" (lower(replace("ErrorFileId", '-', '')));
