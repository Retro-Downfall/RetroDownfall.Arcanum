CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

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
