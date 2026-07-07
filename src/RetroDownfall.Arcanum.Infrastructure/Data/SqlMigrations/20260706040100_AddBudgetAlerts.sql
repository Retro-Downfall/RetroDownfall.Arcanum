CREATE TABLE IF NOT EXISTS "BudgetAlerts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_BudgetAlerts" PRIMARY KEY,
    "Threshold" INTEGER NOT NULL,
    "AlertedAt" TEXT NOT NULL,
    "SpendUsd" NUMERIC NOT NULL,
    "DailyLimitUsd" NUMERIC NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_BudgetAlerts_Threshold_Date" ON "BudgetAlerts" ("Threshold", date("AlertedAt"));
