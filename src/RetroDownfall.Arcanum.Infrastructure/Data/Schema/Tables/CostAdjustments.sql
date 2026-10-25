-- AmountUsd is at the version-9 ADD COLUMN tail so fresh and evolved catalogs converge.
CREATE TABLE IF NOT EXISTS "CostAdjustments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CostAdjustments" PRIMARY KEY,
    "BillableOperationId" TEXT NULL,
    "RunId" TEXT NULL,
    "Reason" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
, "AmountUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("AmountUsd") = 'text'));

CREATE INDEX IF NOT EXISTS "IX_CostAdjustments_Id_Norm" ON "CostAdjustments" (lower(replace("Id", '-', '')));

CREATE INDEX IF NOT EXISTS "IX_CostAdjustments_RunId_Norm" ON "CostAdjustments" (lower(replace("RunId", '-', '')));

CREATE INDEX IF NOT EXISTS "IX_CostAdjustments_BillableOperationId" ON "CostAdjustments" ("BillableOperationId");
