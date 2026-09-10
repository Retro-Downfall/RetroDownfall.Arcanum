-- The two exact USD columns are at the version-9 ADD COLUMN tail so fresh and evolved catalogs
-- converge to the same normalized SQLite definition.
CREATE TABLE IF NOT EXISTS "BudgetReservations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_BudgetReservations" PRIMARY KEY,
    "RunId" TEXT NOT NULL,
    "BudgetPeriod" TEXT NOT NULL,
    "Status" INTEGER NOT NULL,
    "ExpiresAt" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
, "ReservedUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("ReservedUsd") = 'text'),
    "ReconciledUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("ReconciledUsd") = 'text'));

CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_BudgetPeriod_Status" ON "BudgetReservations" ("BudgetPeriod", "Status");
CREATE INDEX IF NOT EXISTS "IX_BudgetReservations_ExpiresAt" ON "BudgetReservations" ("ExpiresAt");
