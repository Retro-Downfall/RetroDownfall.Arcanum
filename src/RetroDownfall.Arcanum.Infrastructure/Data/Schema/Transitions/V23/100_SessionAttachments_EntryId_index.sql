CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_EntryId_Norm" ON "SessionAttachments" (lower(replace("EntryId", '-', '')));
