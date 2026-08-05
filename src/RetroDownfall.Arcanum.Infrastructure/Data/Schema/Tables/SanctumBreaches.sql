CREATE TABLE IF NOT EXISTS "SanctumBreaches" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_SanctumBreaches" PRIMARY KEY,
    "CampaignId" TEXT NOT NULL,
    "OccurredAt" TEXT NOT NULL,
    "ToolName" TEXT NOT NULL,
    "BreachType" TEXT NOT NULL,
    "Description" TEXT NOT NULL,
    "DetailsJson" TEXT NULL,
    CONSTRAINT "FK_SanctumBreaches_Campaigns_CampaignId"
        FOREIGN KEY ("CampaignId") REFERENCES "Campaigns"("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_SanctumBreaches_CampaignId_OccurredAt"
    ON "SanctumBreaches" ("CampaignId", "OccurredAt" DESC);
