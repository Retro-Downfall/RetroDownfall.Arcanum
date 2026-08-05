CREATE TABLE IF NOT EXISTS "Apprentices" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Apprentices" PRIMARY KEY,
    "CampaignId" TEXT NULL,
    "Name" TEXT NOT NULL,
    "Goal" TEXT NOT NULL,
    "Plan" TEXT NOT NULL,
    "CurrentStep" INTEGER NOT NULL,
    "Status" TEXT NOT NULL,
    "SessionId" TEXT NULL,
    "WorkspacePath" TEXT NOT NULL,
    "CheckpointData" TEXT NULL,
    "ErrorMessage" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    CONSTRAINT "FK_Apprentices_Campaigns_CampaignId" FOREIGN KEY ("CampaignId") REFERENCES "Campaigns" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_Apprentices_Sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "Sessions" ("Id") ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS "IX_Apprentices_CampaignId" ON "Apprentices" ("CampaignId");

CREATE INDEX IF NOT EXISTS "IX_Apprentices_SessionId" ON "Apprentices" ("SessionId");

CREATE INDEX IF NOT EXISTS "IX_Apprentices_Status" ON "Apprentices" ("Status");

CREATE INDEX IF NOT EXISTS "IX_Apprentices_UpdatedAt" ON "Apprentices" ("UpdatedAt");
