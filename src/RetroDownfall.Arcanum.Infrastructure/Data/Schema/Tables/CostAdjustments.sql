-- AmountUsd is at the version-9 ADD COLUMN tail so fresh and evolved catalogs converge.
CREATE TABLE IF NOT EXISTS "CostAdjustments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CostAdjustments" PRIMARY KEY,
    "BillableOperationId" TEXT NULL,
    "RunId" TEXT NULL,
    "Reason" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
, "AmountUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("AmountUsd") = 'text'));
