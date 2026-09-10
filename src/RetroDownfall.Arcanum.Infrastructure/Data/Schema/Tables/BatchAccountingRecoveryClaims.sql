CREATE TABLE IF NOT EXISTS "BatchAccountingRecoveryClaims" (
    "BatchId" TEXT NOT NULL CONSTRAINT "PK_BatchAccountingRecoveryClaims" PRIMARY KEY,
    CONSTRAINT "FK_BatchAccountingRecoveryClaims_Batches_BatchId"
        FOREIGN KEY ("BatchId") REFERENCES "Batches" ("Id") ON DELETE CASCADE
);
