UPDATE "BudgetReservations"
SET "ReservedUsd" = CAST("ReservedUsdLegacy" AS TEXT),
    "ReconciledUsd" = CAST("ReconciledUsdLegacy" AS TEXT);
