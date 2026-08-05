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
