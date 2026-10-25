CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_Id_Norm" ON "LongRunningOperations" (lower(replace("Id", '-', '')));
