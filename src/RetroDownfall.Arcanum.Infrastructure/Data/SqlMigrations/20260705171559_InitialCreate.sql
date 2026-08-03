CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "MageSettings" (
    "Key" TEXT NOT NULL CONSTRAINT "PK_MageSettings" PRIMARY KEY,
    "Value" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "WorkspaceContexts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_WorkspaceContexts" PRIMARY KEY,
    "RootPath" TEXT NOT NULL,
    "SerializedSnapshot" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_WorkspaceContexts_RootPath_CreatedAt" ON "WorkspaceContexts" ("RootPath", "CreatedAt");

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

CREATE TABLE IF NOT EXISTS "Sessions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY,
    "CampaignId" TEXT NULL,
    "Title" TEXT NULL,
    "Status" TEXT NOT NULL DEFAULT 'active',
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "Summary" TEXT NULL,
    "LastSummarizedMessageAt" TEXT NULL,
    "TotalTokensUsed" INTEGER NOT NULL DEFAULT 0,
    "TotalCostUsd" NUMERIC NOT NULL DEFAULT 0,
    "UnsummarizedEntryCount" INTEGER NOT NULL DEFAULT 0,
    "ForkedFromSessionId" TEXT NULL
);

CREATE INDEX IF NOT EXISTS "IX_Sessions_CreatedAt" ON "Sessions" ("CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_UpdatedAt" ON "Sessions" ("UpdatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_Status" ON "Sessions" ("Status");

CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId" ON "Sessions" ("CampaignId");

CREATE INDEX IF NOT EXISTS "IX_Sessions_Status_UpdatedAt" ON "Sessions" ("Status", "UpdatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_CampaignId_Status_UpdatedAt" ON "Sessions" ("CampaignId", "Status", "UpdatedAt");

CREATE INDEX IF NOT EXISTS "IX_Sessions_UnsummarizedEntryCount" ON "Sessions" ("UnsummarizedEntryCount");

CREATE INDEX IF NOT EXISTS "IX_Sessions_ForkedFromSessionId" ON "Sessions" ("ForkedFromSessionId");

CREATE TABLE IF NOT EXISTS "Entries" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Entries" PRIMARY KEY,
    "SessionId" TEXT NOT NULL,
    "Role" INTEGER NOT NULL,
    "Content" TEXT NOT NULL,
    "ModelUsed" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "Sequence" INTEGER NOT NULL,
    "ToolCallId" TEXT NULL,
    "ToolName" TEXT NULL,
    "ToolArguments" TEXT NULL,
    CONSTRAINT "FK_Entries_Sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "Sessions" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_CreatedAt" ON "Entries" ("SessionId", "CreatedAt");

-- Authoritative intra-session chronological order. Unique so a lost per-session allocation races
-- into a write failure instead of silently reordering a transcript.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Entries_SessionId_Sequence" ON "Entries" ("SessionId", "Sequence");

CREATE INDEX IF NOT EXISTS "IX_Entries_Role" ON "Entries" ("Role");

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

CREATE TABLE IF NOT EXISTS "UnseenServantWatermarks" (
    "JobKey" TEXT NOT NULL CONSTRAINT "PK_UnseenServantWatermarks" PRIMARY KEY,
    "LastRunAt" TEXT NOT NULL,
    "EffectiveIntervalMinutes" INTEGER NOT NULL DEFAULT 0
);

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

CREATE TABLE IF NOT EXISTS "IdempotencyKeys" (
    "KeyHash" TEXT NOT NULL CONSTRAINT "PK_IdempotencyKeys" PRIMARY KEY,
    "ResponseBody" TEXT NOT NULL,
    "StatusCode" INTEGER NOT NULL,
    "ContentType" TEXT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_IdempotencyKeys_CreatedAt" ON "IdempotencyKeys" ("CreatedAt");

CREATE TABLE IF NOT EXISTS "UploadedFiles" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_UploadedFiles" PRIMARY KEY,
    "Filename" TEXT NOT NULL,
    "Bytes" INTEGER NOT NULL,
    "Purpose" TEXT NOT NULL,
    "MimeType" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_UploadedFiles_Purpose" ON "UploadedFiles" ("Purpose");

CREATE INDEX IF NOT EXISTS "IX_UploadedFiles_CreatedAt" ON "UploadedFiles" ("CreatedAt");

CREATE TABLE IF NOT EXISTS "Batches" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Batches" PRIMARY KEY,
    "InputFileId" TEXT NOT NULL,
    "Endpoint" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CompletedAt" TEXT NULL,
    "OutputFileId" TEXT NULL,
    "ErrorFileId" TEXT NULL,
    "TotalRequestCount" INTEGER NOT NULL DEFAULT 0,
    "CompletedRequestCount" INTEGER NOT NULL DEFAULT 0,
    "FailedRequestCount" INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT "CK_Batches_RequestCounts" CHECK (
        "TotalRequestCount" >= 0
        AND "CompletedRequestCount" >= 0
        AND "FailedRequestCount" >= 0
        AND "CompletedRequestCount" + "FailedRequestCount" <= "TotalRequestCount"
    )
);

CREATE INDEX IF NOT EXISTS "IX_Batches_Status" ON "Batches" ("Status");

CREATE INDEX IF NOT EXISTS "IX_Batches_CreatedAt" ON "Batches" ("CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_Batches_CreatedAt_Id" ON "Batches" ("CreatedAt" DESC, "Id" DESC);

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

CREATE TRIGGER IF NOT EXISTS "TR_BatchLineCheckpoints_IncrementTotal"
AFTER INSERT ON "BatchLineCheckpoints"
BEGIN
    UPDATE "Batches"
    SET "TotalRequestCount" = "TotalRequestCount" + 1
    WHERE "Id" = NEW."BatchId";
END;

CREATE TRIGGER IF NOT EXISTS "TR_BatchLineCheckpoints_IncrementOutcome"
AFTER UPDATE OF "State", "Outcome" ON "BatchLineCheckpoints"
WHEN OLD."State" = 0 AND NEW."State" = 1
BEGIN
    UPDATE "Batches"
    SET "CompletedRequestCount" = "CompletedRequestCount" + CASE WHEN NEW."Outcome" = 0 THEN 1 ELSE 0 END,
        "FailedRequestCount" = "FailedRequestCount" + CASE WHEN NEW."Outcome" = 1 THEN 1 ELSE 0 END
    WHERE "Id" = NEW."BatchId";
END;
