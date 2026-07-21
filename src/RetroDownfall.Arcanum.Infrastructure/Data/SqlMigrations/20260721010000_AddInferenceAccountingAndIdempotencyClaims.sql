CREATE TABLE IF NOT EXISTS "InferenceRuns" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_InferenceRuns" PRIMARY KEY,
    "RequestId" TEXT NOT NULL,
    "SessionId" TEXT NULL,
    "Surface" TEXT NOT NULL,
    "Purpose" TEXT NOT NULL,
    "StartedAt" TEXT NOT NULL,
    "CompletedAt" TEXT NULL,
    "Status" INTEGER NOT NULL,
    "IdempotencyClaimId" TEXT NULL
);

CREATE INDEX IF NOT EXISTS "IX_InferenceRuns_StartedAt" ON "InferenceRuns" ("StartedAt");
CREATE INDEX IF NOT EXISTS "IX_InferenceRuns_IdempotencyClaimId" ON "InferenceRuns" ("IdempotencyClaimId");

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
    "CachedTokens" INTEGER NOT NULL,
    "PricingSnapshotJson" TEXT NOT NULL,
    "ActualCostUsd" NUMERIC NOT NULL,
    "Status" INTEGER NOT NULL,
    "ProviderRequestId" TEXT NULL,
    CONSTRAINT "FK_BillableOperations_InferenceRuns_RunId" FOREIGN KEY ("RunId") REFERENCES "InferenceRuns" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_BillableOperations_CompletedAt" ON "BillableOperations" ("CompletedAt");
CREATE INDEX IF NOT EXISTS "IX_BillableOperations_RunId" ON "BillableOperations" ("RunId");

CREATE TABLE IF NOT EXISTS "BudgetReservations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_BudgetReservations" PRIMARY KEY,
    "RunId" TEXT NOT NULL,
    "BudgetPeriod" TEXT NOT NULL,
    "ReservedUsd" NUMERIC NOT NULL,
    "ReconciledUsd" NUMERIC NOT NULL,
    "Status" INTEGER NOT NULL,
    "ExpiresAt" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_BudgetPeriod_Status" ON "BudgetReservations" ("BudgetPeriod", "Status");
CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_ExpiresAt" ON "BudgetReservations" ("ExpiresAt");

CREATE TABLE IF NOT EXISTS "CostAdjustments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CostAdjustments" PRIMARY KEY,
    "BillableOperationId" TEXT NULL,
    "RunId" TEXT NULL,
    "AmountUsd" NUMERIC NOT NULL,
    "Reason" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "IdempotencyClaims" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_IdempotencyClaims" PRIMARY KEY,
    "ClaimKeyHash" TEXT NOT NULL,
    "FingerprintHash" TEXT NOT NULL,
    "State" INTEGER NOT NULL,
    "OwnerId" TEXT NOT NULL,
    "LeaseExpiresAt" TEXT NOT NULL,
    "HeartbeatAt" TEXT NOT NULL,
    "RunId" TEXT NULL,
    "StatusCode" INTEGER NULL,
    "ContentType" TEXT NULL,
    "ResponseBody" TEXT NULL,
    "TerminalStreamComplete" INTEGER NOT NULL DEFAULT 0,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_IdempotencyClaims_ClaimKeyHash" ON "IdempotencyClaims" ("ClaimKeyHash");
CREATE INDEX IF NOT EXISTS "IX_IdempotencyClaims_State_LeaseExpiresAt" ON "IdempotencyClaims" ("State", "LeaseExpiresAt");
