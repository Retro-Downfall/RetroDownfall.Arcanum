CREATE INDEX IF NOT EXISTS "IX_CostAdjustments_RunId_Norm" ON "CostAdjustments" (lower(replace("RunId", '-', '')));
