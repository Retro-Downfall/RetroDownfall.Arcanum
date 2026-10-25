CREATE INDEX IF NOT EXISTS "IX_BillableOperations_RunId_Norm" ON "BillableOperations" (lower(replace("RunId", '-', '')));
