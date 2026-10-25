CREATE INDEX IF NOT EXISTS "IX_BudgetAlerts_Id_Norm" ON "BudgetAlerts" (lower(replace("Id", '-', '')));
