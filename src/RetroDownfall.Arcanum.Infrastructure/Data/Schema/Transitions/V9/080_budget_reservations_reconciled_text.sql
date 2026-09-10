ALTER TABLE "BudgetReservations"
ADD COLUMN "ReconciledUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("ReconciledUsd") = 'text');
