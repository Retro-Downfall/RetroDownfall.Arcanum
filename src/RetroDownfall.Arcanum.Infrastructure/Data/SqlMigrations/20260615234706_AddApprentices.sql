CREATE TABLE "Apprentices" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Apprentices" PRIMARY KEY,
    "CampaignId" TEXT NULL,
    "Name" TEXT NOT NULL,
    "Goal" TEXT NOT NULL,
    "Plan" TEXT NOT NULL,
    "CurrentStep" INTEGER NOT NULL,
    "Status" TEXT NOT NULL,
    "ConversationId" TEXT NULL,
    "WorkspacePath" TEXT NOT NULL,
    "CheckpointData" TEXT NULL,
    "ErrorMessage" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    CONSTRAINT "FK_Apprentices_Campaigns_CampaignId" FOREIGN KEY ("CampaignId") REFERENCES "Campaigns" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_Apprentices_Conversations_ConversationId" FOREIGN KEY ("ConversationId") REFERENCES "Conversations" ("Id") ON DELETE SET NULL
);

CREATE INDEX "IX_Apprentices_CampaignId" ON "Apprentices" ("CampaignId");

CREATE INDEX "IX_Apprentices_ConversationId" ON "Apprentices" ("ConversationId");

CREATE INDEX "IX_Apprentices_Status" ON "Apprentices" ("Status");
