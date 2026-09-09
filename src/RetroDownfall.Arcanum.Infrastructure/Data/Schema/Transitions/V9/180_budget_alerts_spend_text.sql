ALTER TABLE "BudgetAlerts"
ADD COLUMN "SpendUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("SpendUsd") = 'text');
