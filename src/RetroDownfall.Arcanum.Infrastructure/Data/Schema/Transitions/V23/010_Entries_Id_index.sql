CREATE INDEX IF NOT EXISTS "IX_Entries_Id_Norm" ON "Entries" (lower(replace("Id", '-', '')));
