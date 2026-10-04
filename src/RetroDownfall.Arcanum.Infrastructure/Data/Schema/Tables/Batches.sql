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
