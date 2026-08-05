CREATE TABLE IF NOT EXISTS "BudgetReservations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_BudgetReservations" PRIMARY KEY,
    "RunId" TEXT NOT NULL,
    "BudgetPeriod" TEXT NOT NULL,
    "ReservedUsd" NUMERIC NOT NULL,
    "ReconciledUsd" NUMERIC NOT NULL,
    "Status" INTEGER NOT NULL,
    "ExpiresAt" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_BudgetPeriod_Status" ON "BudgetReservations" ("BudgetPeriod", "Status");
CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_ExpiresAt" ON "BudgetReservations" ("ExpiresAt");
