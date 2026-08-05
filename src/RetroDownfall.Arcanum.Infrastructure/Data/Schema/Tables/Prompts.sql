CREATE TABLE IF NOT EXISTS "Prompts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Prompts" PRIMARY KEY,
    "CampaignId" TEXT NULL,
    "Name" TEXT NOT NULL,
    "Version" TEXT NOT NULL,
    "Description" TEXT NULL,
    "Tags" TEXT NOT NULL,
    "Template" TEXT NOT NULL,
    "ParameterSchema" TEXT NULL,
    "DefaultParameters" TEXT NULL,
    "Model" TEXT NULL,
    "Provider" TEXT NULL,
    "Temperature" REAL NULL,
    "TopP" REAL NULL,
    "MaxOutputTokens" INTEGER NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    CONSTRAINT "FK_Prompts_Campaigns_CampaignId" FOREIGN KEY ("CampaignId") REFERENCES "Campaigns" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_Prompts_CampaignId_Name" ON "Prompts" ("CampaignId", "Name");

CREATE UNIQUE INDEX IF NOT EXISTS IX_Prompts_Name_Version_Global ON Prompts(Name, Version) WHERE CampaignId IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS IX_Prompts_Name_Version_Campaign ON Prompts(Name, Version, CampaignId) WHERE CampaignId IS NOT NULL;
