CREATE INDEX IF NOT EXISTS "IX_LongRunningOperations_SessionId_Norm" ON "LongRunningOperations" (lower(replace("SessionId", '-', '')));
