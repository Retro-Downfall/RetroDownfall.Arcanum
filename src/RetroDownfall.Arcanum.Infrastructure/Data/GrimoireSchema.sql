CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

BEGIN TRANSACTION;
CREATE TABLE "Conversations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Conversations" PRIMARY KEY,
    "CreatedAt" TEXT NOT NULL,
    "Title" TEXT NOT NULL
);

CREATE TABLE "MageSettings" (
    "Key" TEXT NOT NULL CONSTRAINT "PK_MageSettings" PRIMARY KEY,
    "Value" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE TABLE "WorkspaceContexts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_WorkspaceContexts" PRIMARY KEY,
    "RootPath" TEXT NOT NULL,
    "ProjectSummary" TEXT NOT NULL,
    "LastScanned" TEXT NOT NULL
);

CREATE TABLE "ChatMessages" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ChatMessages" PRIMARY KEY,
    "ConversationId" TEXT NOT NULL,
    "Role" INTEGER NOT NULL,
    "Content" TEXT NOT NULL,
    "ModelUsed" TEXT NOT NULL,
    "Timestamp" TEXT NOT NULL,
    CONSTRAINT "FK_ChatMessages_Conversations_ConversationId" FOREIGN KEY ("ConversationId") REFERENCES "Conversations" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_ChatMessages_ConversationId_Timestamp" ON "ChatMessages" ("ConversationId", "Timestamp");

CREATE INDEX "IX_Conversations_CreatedAt" ON "Conversations" ("CreatedAt");

CREATE INDEX "IX_WorkspaceContexts_RootPath" ON "WorkspaceContexts" ("RootPath");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260508212137_InitialCreate', '10.0.8');

COMMIT;

BEGIN TRANSACTION;

                CREATE VIRTUAL TABLE IF NOT EXISTS ChatMessages_fts USING fts5(
                    Id UNINDEXED,
                    ConversationId UNINDEXED,
                    Role UNINDEXED,
                    Content
                );

                CREATE TRIGGER IF NOT EXISTS ChatMessages_ai AFTER INSERT ON ChatMessages BEGIN
                    INSERT INTO ChatMessages_fts(Id, ConversationId, Role, Content)
                    VALUES (new.Id, new.ConversationId, new.Role, new.Content);
                END;

                CREATE TRIGGER IF NOT EXISTS ChatMessages_ad AFTER DELETE ON ChatMessages BEGIN
                    DELETE FROM ChatMessages_fts WHERE Id = old.Id;
                END;

                CREATE TRIGGER IF NOT EXISTS ChatMessages_au AFTER UPDATE ON ChatMessages BEGIN
                    DELETE FROM ChatMessages_fts WHERE Id = old.Id;
                    INSERT INTO ChatMessages_fts(Id, ConversationId, Role, Content)
                    VALUES (new.Id, new.ConversationId, new.Role, new.Content);
                END;

                INSERT INTO ChatMessages_fts(Id, ConversationId, Role, Content)
                SELECT Id, ConversationId, Role, Content FROM ChatMessages;
            

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260508215834_AddChatMessagesFts', '10.0.8');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "Conversations" ADD "LastSummarizedMessageAt" TEXT NULL;

ALTER TABLE "Conversations" ADD "Summary" TEXT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260509005818_AddCampaignLogFields', '10.0.8');

COMMIT;

BEGIN TRANSACTION;
DROP INDEX "IX_WorkspaceContexts_RootPath";

ALTER TABLE "WorkspaceContexts" RENAME COLUMN "ProjectSummary" TO "SerializedSnapshot";

ALTER TABLE "WorkspaceContexts" RENAME COLUMN "LastScanned" TO "CreatedAt";

CREATE INDEX "IX_WorkspaceContexts_RootPath_CreatedAt" ON "WorkspaceContexts" ("RootPath", "CreatedAt");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260509195722_EvolveWorkspaceContextForChronosync', '10.0.8');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "Conversations" ADD "TotalTokensUsed" INTEGER NOT NULL DEFAULT 0;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260510205005_AddTotalTokensUsed', '10.0.8');

COMMIT;

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

BEGIN TRANSACTION;
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

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260615234706_AddApprentices', '10.0.8');

COMMIT;

BEGIN TRANSACTION;
ALTER TABLE "Campaigns" ADD "SanctumConfigJson" TEXT NOT NULL DEFAULT '{}';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260616001002_AddCampaignSanctumConfig', '10.0.8');

COMMIT;

BEGIN TRANSACTION;
DROP TRIGGER IF EXISTS ChatMessages_ai;
DROP TRIGGER IF EXISTS ChatMessages_ad;
DROP TRIGGER IF EXISTS ChatMessages_au;
DROP TABLE IF EXISTS ChatMessages_fts;

ALTER TABLE "Conversations" RENAME TO "Sessions";

ALTER TABLE "ChatMessages" RENAME TO "Entries";

DROP INDEX "IX_Conversations_CreatedAt";

CREATE INDEX "IX_Sessions_CreatedAt" ON "Sessions" ("CreatedAt");

ALTER TABLE "Entries" RENAME COLUMN "ConversationId" TO "SessionId";

ALTER TABLE "Entries" RENAME COLUMN "Timestamp" TO "CreatedAt";

DROP INDEX "IX_ChatMessages_ConversationId_Timestamp";

CREATE INDEX "IX_Entries_SessionId_CreatedAt" ON "Entries" ("SessionId", "CreatedAt");

ALTER TABLE "Apprentices" RENAME COLUMN "ConversationId" TO "SessionId";

DROP INDEX "IX_Apprentices_ConversationId";

CREATE INDEX "IX_Apprentices_SessionId" ON "Apprentices" ("SessionId");

ALTER TABLE "Sessions" ADD "CampaignId" TEXT NULL;

ALTER TABLE "Sessions" ADD "Status" TEXT NOT NULL DEFAULT 'active';

ALTER TABLE "Sessions" ADD "UpdatedAt" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00+00:00';

UPDATE "Sessions" SET "UpdatedAt" = "CreatedAt" WHERE "UpdatedAt" = '0001-01-01T00:00:00+00:00';

ALTER TABLE "Entries" ADD "ToolArguments" TEXT NULL;

ALTER TABLE "Entries" ADD "ToolCallId" TEXT NULL;

ALTER TABLE "Entries" ADD "ToolName" TEXT NULL;

CREATE INDEX "IX_Sessions_Status" ON "Sessions" ("Status");

CREATE INDEX "IX_Sessions_UpdatedAt" ON "Sessions" ("UpdatedAt");

CREATE VIRTUAL TABLE IF NOT EXISTS Entries_fts USING fts5(
    Id UNINDEXED,
    SessionId UNINDEXED,
    Role UNINDEXED,
    Content
);

CREATE TRIGGER IF NOT EXISTS Entries_ai AFTER INSERT ON Entries BEGIN
    INSERT INTO Entries_fts(Id, SessionId, Role, Content)
    VALUES (new.Id, new.SessionId, new.Role, new.Content);
END;

CREATE TRIGGER IF NOT EXISTS Entries_ad AFTER DELETE ON Entries BEGIN
    DELETE FROM Entries_fts WHERE Id = old.Id;
END;

CREATE TRIGGER IF NOT EXISTS Entries_au AFTER UPDATE ON Entries BEGIN
    DELETE FROM Entries_fts WHERE Id = old.Id;
    INSERT INTO Entries_fts(Id, SessionId, Role, Content)
    VALUES (new.Id, new.SessionId, new.Role, new.Content);
END;

INSERT INTO Entries_fts(Id, SessionId, Role, Content)
SELECT Id, SessionId, Role, Content FROM Entries;

CREATE TABLE "ef_temp_Apprentices" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Apprentices" PRIMARY KEY,
    "CampaignId" TEXT NULL,
    "CheckpointData" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CurrentStep" INTEGER NOT NULL,
    "ErrorMessage" TEXT NULL,
    "Goal" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Plan" TEXT NOT NULL,
    "SessionId" TEXT NULL,
    "Status" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "WorkspacePath" TEXT NOT NULL,
    CONSTRAINT "FK_Apprentices_Campaigns_CampaignId" FOREIGN KEY ("CampaignId") REFERENCES "Campaigns" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_Apprentices_Sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "Sessions" ("Id") ON DELETE SET NULL
);

INSERT INTO "ef_temp_Apprentices" ("Id", "CampaignId", "CheckpointData", "CreatedAt", "CurrentStep", "ErrorMessage", "Goal", "Name", "Plan", "SessionId", "Status", "UpdatedAt", "WorkspacePath")
SELECT "Id", "CampaignId", "CheckpointData", "CreatedAt", "CurrentStep", "ErrorMessage", "Goal", "Name", "Plan", "SessionId", "Status", "UpdatedAt", "WorkspacePath"
FROM "Apprentices";

CREATE TABLE "ef_temp_Entries" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Entries" PRIMARY KEY,
    "Content" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "ModelUsed" TEXT NOT NULL,
    "Role" INTEGER NOT NULL,
    "SessionId" TEXT NOT NULL,
    "ToolArguments" TEXT NULL,
    "ToolCallId" TEXT NULL,
    "ToolName" TEXT NULL,
    CONSTRAINT "FK_Entries_Sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "Sessions" ("Id") ON DELETE CASCADE
);

INSERT INTO "ef_temp_Entries" ("Id", "Content", "CreatedAt", "ModelUsed", "Role", "SessionId", "ToolArguments", "ToolCallId", "ToolName")
SELECT "Id", "Content", "CreatedAt", "ModelUsed", "Role", "SessionId", "ToolArguments", "ToolCallId", "ToolName"
FROM "Entries";

CREATE TABLE "ef_temp_Sessions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY,
    "CampaignId" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "LastSummarizedMessageAt" TEXT NULL,
    "Status" TEXT NOT NULL DEFAULT 'active',
    "Summary" TEXT NULL,
    "Title" TEXT NULL,
    "TotalTokensUsed" INTEGER NOT NULL DEFAULT 0,
    "UpdatedAt" TEXT NOT NULL
);

INSERT INTO "ef_temp_Sessions" ("Id", "CampaignId", "CreatedAt", "LastSummarizedMessageAt", "Status", "Summary", "Title", "TotalTokensUsed", "UpdatedAt")
SELECT "Id", "CampaignId", "CreatedAt", "LastSummarizedMessageAt", "Status", "Summary", "Title", "TotalTokensUsed", "UpdatedAt"
FROM "Sessions";

COMMIT;

PRAGMA foreign_keys = 0;

BEGIN TRANSACTION;
DROP TABLE "Apprentices";

ALTER TABLE "ef_temp_Apprentices" RENAME TO "Apprentices";

DROP TABLE "Entries";

ALTER TABLE "ef_temp_Entries" RENAME TO "Entries";

DROP TABLE "Sessions";

ALTER TABLE "ef_temp_Sessions" RENAME TO "Sessions";

COMMIT;

PRAGMA foreign_keys = 1;

BEGIN TRANSACTION;
CREATE INDEX "IX_Apprentices_CampaignId" ON "Apprentices" ("CampaignId");

CREATE INDEX "IX_Apprentices_SessionId" ON "Apprentices" ("SessionId");

CREATE INDEX "IX_Apprentices_Status" ON "Apprentices" ("Status");

CREATE INDEX "IX_Entries_SessionId_CreatedAt" ON "Entries" ("SessionId", "CreatedAt");

CREATE INDEX "IX_Sessions_CreatedAt" ON "Sessions" ("CreatedAt");

CREATE INDEX "IX_Sessions_Status" ON "Sessions" ("Status");

CREATE INDEX "IX_Sessions_UpdatedAt" ON "Sessions" ("UpdatedAt");

COMMIT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260616010843_RenameSessionAndEntry', '10.0.8');

