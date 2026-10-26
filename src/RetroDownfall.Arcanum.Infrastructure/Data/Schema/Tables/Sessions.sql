-- TotalCostUsd is laid out exactly as SQLite splices an added column into the stored table
-- declaration. Version 8 replaces the former NUMERIC-affinity column because SQLite coerced EF's
-- exact decimal text to an IEEE-754 REAL before storing it. The replacement TEXT column keeps the
-- decimal(18,8) value exact, and fresh and evolved installations must normalize to the same DDL.
CREATE TABLE IF NOT EXISTS "Sessions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY,
    "CampaignId" TEXT NULL,
    "Title" TEXT NULL,
    "Status" TEXT NOT NULL DEFAULT 'active',
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "Summary" TEXT NULL,
    "LastSummarizedMessageAt" TEXT NULL,
    "TotalTokensUsed" INTEGER NOT NULL DEFAULT 0,
    "UnsummarizedEntryCount" INTEGER NOT NULL DEFAULT 0,
    "ForkedFromSessionId" TEXT NULL
, "TotalCostUsd" TEXT NOT NULL DEFAULT 0);

CREATE INDEX IF NOT EXISTS "IX_Sessions_CreatedAt" ON "Sessions" ("CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_UpdatedAt" ON "Sessions" ("UpdatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_Status" ON "Sessions" ("Status");

CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId" ON "Sessions" ("CampaignId");

CREATE INDEX IF NOT EXISTS "IX_Sessions_Status_UpdatedAt" ON "Sessions" ("Status", "UpdatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId_Status_UpdatedAt" ON "Sessions" ("CampaignId", "Status", "UpdatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_UnsummarizedEntryCount" ON "Sessions" ("UnsummarizedEntryCount");

CREATE INDEX IF NOT EXISTS "IX_Sessions_ForkedFromSessionId" ON "Sessions" ("ForkedFromSessionId");

CREATE INDEX IF NOT EXISTS "IX_Sessions_Id_Norm" ON "Sessions" (lower(replace("Id", '-', '')));

CREATE INDEX IF NOT EXISTS idx_memory_search_Sessions_order ON Sessions(UpdatedAt DESC,Id DESC) WHERE Summary IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_memory_search_Sessions_owner ON Sessions(Id,UpdatedAt DESC,Id DESC);
