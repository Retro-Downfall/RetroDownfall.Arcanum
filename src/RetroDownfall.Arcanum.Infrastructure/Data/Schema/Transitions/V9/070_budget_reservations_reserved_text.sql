ALTER TABLE "BudgetReservations"
ADD COLUMN "ReservedUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("ReservedUsd") = 'text');
