CREATE TABLE IF NOT EXISTS "LongRunningOperations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_LongRunningOperations" PRIMARY KEY,
    "Kind" TEXT NOT NULL,
    "State" INTEGER NOT NULL,
    "RecoveryPolicy" INTEGER NOT NULL,
    "RootOperationId" TEXT NULL,
    "ParentOperationId" TEXT NULL,
    "SessionId" TEXT NULL,
    "RunId" TEXT NULL,
    "InferenceRunId" TEXT NULL,
    "BudgetReservationId" TEXT NULL,
    "IdempotencyClaimId" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "StartedAt" TEXT NULL,
    "HeartbeatAt" TEXT NULL,
    "CompletedAt" TEXT NULL,
    "LeaseOwner" TEXT NULL,
    "LeaseExpiresAt" TEXT NULL,
    "AttemptCount" INTEGER NOT NULL DEFAULT 0,
    "CheckpointVersion" INTEGER NOT NULL DEFAULT 0,
    "CheckpointPayload" BLOB NULL,
    "CheckpointReference" TEXT NULL,
    "PublicSummary" TEXT NOT NULL,
    "TerminalErrorCode" TEXT NULL,
    "Revision" INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT "FK_LongRunningOperations_Root"
        FOREIGN KEY ("RootOperationId") REFERENCES "LongRunningOperations" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_LongRunningOperations_Parent"
        FOREIGN KEY ("ParentOperationId") REFERENCES "LongRunningOperations" ("Id") ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_State_LeaseExpiresAt"
    ON "LongRunningOperations" ("State", "LeaseExpiresAt");

CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_Kind_State"
    ON "LongRunningOperations" ("Kind", "State");

CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_ParentOperationId"
    ON "LongRunningOperations" ("ParentOperationId");

CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_SessionId"
    ON "LongRunningOperations" ("SessionId");

CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_RunId"
    ON "LongRunningOperations" ("RunId");

CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_BudgetReservationId"
    ON "LongRunningOperations" ("BudgetReservationId");
