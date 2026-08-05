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
