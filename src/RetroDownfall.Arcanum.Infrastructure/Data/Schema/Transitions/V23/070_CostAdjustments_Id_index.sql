CREATE INDEX IF NOT EXISTS "IX_CostAdjustments_Id_Norm" ON "CostAdjustments" (lower(replace("Id", '-', '')));
