CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_RunId_Norm" ON "BudgetReservations" (lower(replace("RunId", '-', '')));
