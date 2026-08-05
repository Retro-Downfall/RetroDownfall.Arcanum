CREATE TABLE IF NOT EXISTS "Campaigns" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Campaigns" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "NameLower" TEXT NOT NULL,
    "Path" TEXT NOT NULL,
    "Type" INTEGER NOT NULL,
    "Description" TEXT NULL,
    "Settings" TEXT NOT NULL,
    "SanctumConfigJson" TEXT NOT NULL DEFAULT '{}',
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Campaigns_NameLower" ON "Campaigns" ("NameLower");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Campaigns_Path" ON "Campaigns" ("Path");

CREATE INDEX IF NOT EXISTS "IX_Campaigns_Type" ON "Campaigns" ("Type");
