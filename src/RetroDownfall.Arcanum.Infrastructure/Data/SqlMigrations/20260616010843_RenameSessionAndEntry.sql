PRAGMA defer_foreign_keys=ON;

DROP TRIGGER IF EXISTS ChatMessages_ai;
DROP TRIGGER IF EXISTS ChatMessages_ad;
DROP TRIGGER IF EXISTS ChatMessages_au;
DROP TABLE IF EXISTS ChatMessages_fts;

ALTER TABLE "Conversations" RENAME TO "Sessions";

ALTER TABLE "ChatMessages" RENAME TO "Entries";

DROP INDEX IF EXISTS "IX_Conversations_CreatedAt";

CREATE INDEX IF NOT EXISTS "IX_Sessions_CreatedAt" ON "Sessions" ("CreatedAt");

ALTER TABLE "Entries" RENAME COLUMN "ConversationId" TO "SessionId";

ALTER TABLE "Entries" RENAME COLUMN "Timestamp" TO "CreatedAt";

DROP INDEX IF EXISTS "IX_ChatMessages_ConversationId_Timestamp";

CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_CreatedAt" ON "Entries" ("SessionId", "CreatedAt");

ALTER TABLE "Apprentices" RENAME COLUMN "ConversationId" TO "SessionId";

DROP INDEX IF EXISTS "IX_Apprentices_ConversationId";

CREATE INDEX IF NOT EXISTS "IX_Apprentices_SessionId" ON "Apprentices" ("SessionId");

ALTER TABLE "Sessions" ADD "CampaignId" TEXT NULL;

ALTER TABLE "Sessions" ADD "Status" TEXT NOT NULL DEFAULT 'active';

ALTER TABLE "Sessions" ADD "UpdatedAt" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00+00:00';

UPDATE "Sessions" SET "UpdatedAt" = "CreatedAt" WHERE "UpdatedAt" LIKE '0001-01-01%';

ALTER TABLE "Entries" ADD "ToolArguments" TEXT NULL;

ALTER TABLE "Entries" ADD "ToolCallId" TEXT NULL;

ALTER TABLE "Entries" ADD "ToolName" TEXT NULL;

CREATE INDEX IF NOT EXISTS "IX_Sessions_Status" ON "Sessions" ("Status");

CREATE INDEX IF NOT EXISTS "IX_Sessions_UpdatedAt" ON "Sessions" ("UpdatedAt");

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

DROP TABLE "Apprentices";

ALTER TABLE "ef_temp_Apprentices" RENAME TO "Apprentices";

DROP TABLE "Entries";

ALTER TABLE "ef_temp_Entries" RENAME TO "Entries";

DROP TABLE "Sessions";

ALTER TABLE "ef_temp_Sessions" RENAME TO "Sessions";

CREATE INDEX IF NOT EXISTS "IX_Apprentices_CampaignId" ON "Apprentices" ("CampaignId");

CREATE INDEX IF NOT EXISTS "IX_Apprentices_SessionId" ON "Apprentices" ("SessionId");

CREATE INDEX IF NOT EXISTS "IX_Apprentices_Status" ON "Apprentices" ("Status");

CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_CreatedAt" ON "Entries" ("SessionId", "CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_CreatedAt" ON "Sessions" ("CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_Status" ON "Sessions" ("Status");

CREATE INDEX IF NOT EXISTS "IX_Sessions_UpdatedAt" ON "Sessions" ("UpdatedAt");

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

INSERT OR IGNORE INTO Entries_fts(Id, SessionId, Role, Content)
SELECT Id, SessionId, Role, Content FROM Entries;
