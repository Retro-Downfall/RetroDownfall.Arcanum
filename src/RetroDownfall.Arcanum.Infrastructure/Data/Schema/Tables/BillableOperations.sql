CREATE TABLE IF NOT EXISTS "BillableOperations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_BillableOperations" PRIMARY KEY,
    "RunId" TEXT NOT NULL,
    "OperationType" INTEGER NOT NULL,
    "Provider" TEXT NOT NULL,
    "Model" TEXT NOT NULL,
    "Purpose" TEXT NOT NULL,
    "StartedAt" TEXT NOT NULL,
    "CompletedAt" TEXT NOT NULL,
    "InputTokens" INTEGER NOT NULL,
    "OutputTokens" INTEGER NOT NULL,
    "ReasoningTokens" INTEGER NOT NULL DEFAULT 0,
    "CachedTokens" INTEGER NOT NULL,
    "PricingSnapshotJson" TEXT NOT NULL,
    "ActualCostUsd" NUMERIC NOT NULL,
    "Status" INTEGER NOT NULL,
    "ProviderRequestId" TEXT NULL,
    CONSTRAINT "FK_BillableOperations_InferenceRuns_RunId" FOREIGN KEY ("RunId") REFERENCES "InferenceRuns" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_BillableOperations_CompletedAt" ON "BillableOperations" ("CompletedAt");
CREATE INDEX IF NOT EXISTS "IX_BillableOperations_RunId" ON "BillableOperations" ("RunId");
