ALTER TABLE "BillableOperations"
ADD COLUMN "ActualCostUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("ActualCostUsd") = 'text');
