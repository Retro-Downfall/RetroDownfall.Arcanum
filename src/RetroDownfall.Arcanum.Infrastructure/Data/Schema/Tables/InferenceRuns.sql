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

-- Retention compares SessionId normalized - lower(replace(SessionId, '-', '')) - because TurnRunWriter writes
-- this column dash-free while Sessions.Id is dashed, once per candidate Session in the planning pass, again
-- in the apply pass, and in the post-commit counts that prove a Session delete. SQLite cannot answer a
-- function-wrapped column from an ordinary column index, so each of those was a scan of the run ledger,
-- which gains a row per turn. The expression here has to stay character for character the shape the
-- predicate has, because that is how SQLite decides the index applies.
CREATE INDEX IF NOT EXISTS "IX_InferenceRuns_SessionId_Norm"
  ON "InferenceRuns" (lower(replace("SessionId", '-', '')));
