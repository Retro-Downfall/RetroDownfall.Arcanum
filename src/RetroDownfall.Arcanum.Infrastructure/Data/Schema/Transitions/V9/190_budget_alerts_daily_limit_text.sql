ALTER TABLE "BudgetAlerts"
ADD COLUMN "DailyLimitUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("DailyLimitUsd") = 'text');
