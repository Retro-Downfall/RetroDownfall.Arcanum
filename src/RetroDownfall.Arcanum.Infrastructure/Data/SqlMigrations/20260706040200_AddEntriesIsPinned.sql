ALTER TABLE "Entries" ADD COLUMN "IsPinned" INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_IsPinned" ON "Entries" ("SessionId", "IsPinned");
