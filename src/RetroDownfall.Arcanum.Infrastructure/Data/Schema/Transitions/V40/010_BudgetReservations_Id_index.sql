CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_Id_Norm" ON "BudgetReservations" (lower(replace("Id", '-', '')));
