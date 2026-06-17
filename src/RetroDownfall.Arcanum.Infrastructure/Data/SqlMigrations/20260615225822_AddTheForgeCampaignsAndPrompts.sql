BEGIN TRANSACTION;
CREATE TABLE "Campaigns" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Campaigns" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "NameLower" TEXT NOT NULL,
    "Path" TEXT NOT NULL,
    "Type" INTEGER NOT NULL,
    "Description" TEXT NULL,
    "Settings" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE TABLE "Prompts" (
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

CREATE UNIQUE INDEX "IX_Campaigns_NameLower" ON "Campaigns" ("NameLower");

CREATE UNIQUE INDEX "IX_Campaigns_Path" ON "Campaigns" ("Path");

CREATE INDEX "IX_Campaigns_Type" ON "Campaigns" ("Type");

CREATE INDEX "IX_Prompts_CampaignId_Name" ON "Prompts" ("CampaignId", "Name");

CREATE UNIQUE INDEX IX_Prompts_Name_Version_Global ON Prompts(Name, Version) WHERE CampaignId IS NULL;
CREATE UNIQUE INDEX IX_Prompts_Name_Version_Campaign ON Prompts(Name, Version, CampaignId) WHERE CampaignId IS NOT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260615225822_AddTheForgeCampaignsAndPrompts', '10.0.8');

COMMIT;

