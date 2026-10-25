CREATE INDEX IF NOT EXISTS "IX_Batches_Id_Norm" ON "Batches" (lower(replace("Id", '-', '')));
