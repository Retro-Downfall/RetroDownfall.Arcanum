ALTER TABLE "CostAdjustments"
ADD COLUMN "AmountUsd" TEXT NOT NULL DEFAULT '0' CHECK (typeof("AmountUsd") = 'text');
