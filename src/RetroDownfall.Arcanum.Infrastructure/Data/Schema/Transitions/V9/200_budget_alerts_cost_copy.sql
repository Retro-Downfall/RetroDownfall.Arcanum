UPDATE "BudgetAlerts"
SET "SpendUsd" = CAST("SpendUsdLegacy" AS TEXT),
    "DailyLimitUsd" = CAST("DailyLimitUsdLegacy" AS TEXT);
