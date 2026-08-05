CREATE TABLE IF NOT EXISTS "IdempotencyKeys" (
    "KeyHash" TEXT NOT NULL CONSTRAINT "PK_IdempotencyKeys" PRIMARY KEY,
    "ResponseBody" TEXT NOT NULL,
    "StatusCode" INTEGER NOT NULL,
    "ContentType" TEXT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_IdempotencyKeys_CreatedAt" ON "IdempotencyKeys" ("CreatedAt");
