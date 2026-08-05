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
