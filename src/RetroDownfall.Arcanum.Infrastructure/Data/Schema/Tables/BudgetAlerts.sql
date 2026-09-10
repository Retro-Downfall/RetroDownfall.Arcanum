-- Exact USD columns are at the version-9 ADD COLUMN tail so fresh and evolved catalogs converge.
CREATE TABLE IF NOT EXISTS "BudgetAlerts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_BudgetAlerts" PRIMARY KEY,
    "Threshold" INTEGER NOT NULL,
    "AlertedAt" TEXT NOT NULL,
    "SpendUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("SpendUsd") = 'text'),
    "DailyLimitUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("DailyLimitUsd") = 'text'));

CREATE UNIQUE INDEX IF NOT EXISTS "IX_BudgetAlerts_Threshold_Date" ON "BudgetAlerts" ("Threshold", date("AlertedAt"));
