CREATE TABLE IF NOT EXISTS "BatchLineCheckpoints" (
    "BatchId" TEXT NOT NULL,
    "LineNumber" INTEGER NOT NULL,
    "CustomId" TEXT NOT NULL,
    "State" INTEGER NOT NULL,
    "OutputKind" INTEGER NULL,
    "Outcome" INTEGER NULL,
    "JsonLine" TEXT NULL,
    "DispatchedAt" TEXT NOT NULL,
    "CompletedAt" TEXT NULL,
    CONSTRAINT "PK_BatchLineCheckpoints" PRIMARY KEY ("BatchId", "LineNumber"),
    CONSTRAINT "FK_BatchLineCheckpoints_Batches_BatchId" FOREIGN KEY ("BatchId") REFERENCES "Batches" ("Id") ON DELETE CASCADE,
    CONSTRAINT "CK_BatchLineCheckpoints_LineNumber" CHECK ("LineNumber" > 0),
    CONSTRAINT "CK_BatchLineCheckpoints_State" CHECK ("State" IN (0, 1)),
    CONSTRAINT "CK_BatchLineCheckpoints_OutputKind" CHECK ("OutputKind" IS NULL OR "OutputKind" IN (0, 1)),
    CONSTRAINT "CK_BatchLineCheckpoints_Outcome" CHECK ("Outcome" IS NULL OR "Outcome" IN (0, 1)),
    CONSTRAINT "CK_BatchLineCheckpoints_TerminalShape" CHECK (
        ("State" = 0 AND "OutputKind" IS NULL AND "Outcome" IS NULL AND "JsonLine" IS NULL AND "CompletedAt" IS NULL)
        OR ("State" = 1 AND "OutputKind" IS NOT NULL AND "Outcome" IS NOT NULL AND "JsonLine" IS NOT NULL AND "CompletedAt" IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS "IX_BatchLineCheckpoints_BatchId_State_LineNumber"
ON "BatchLineCheckpoints" ("BatchId", "State", "LineNumber");
