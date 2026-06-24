ALTER TABLE "Sessions" ADD COLUMN "UnsummarizedEntryCount" INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId" ON "Sessions" ("CampaignId");

CREATE INDEX IF NOT EXISTS "IX_Sessions_Status_UpdatedAt" ON "Sessions" ("Status", "UpdatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId_Status_UpdatedAt" ON "Sessions" ("CampaignId", "Status", "UpdatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_UnsummarizedEntryCount" ON "Sessions" ("UnsummarizedEntryCount");

CREATE INDEX IF NOT EXISTS "IX_Entries_Role" ON "Entries" ("Role");

CREATE INDEX IF NOT EXISTS "IX_Apprentices_UpdatedAt" ON "Apprentices" ("UpdatedAt");
