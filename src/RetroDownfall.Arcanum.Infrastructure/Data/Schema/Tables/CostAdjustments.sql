CREATE TABLE IF NOT EXISTS "CostAdjustments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CostAdjustments" PRIMARY KEY,
    "BillableOperationId" TEXT NULL,
    "RunId" TEXT NULL,
    "AmountUsd" NUMERIC NOT NULL,
    "Reason" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);
