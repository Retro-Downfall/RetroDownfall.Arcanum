UPDATE "BillableOperations"
SET "ActualCostUsd" = CAST("ActualCostUsdLegacy" AS TEXT);
