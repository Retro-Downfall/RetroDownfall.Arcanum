CREATE TABLE IF NOT EXISTS "WorkspaceContexts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_WorkspaceContexts" PRIMARY KEY,
    "RootPath" TEXT NOT NULL,
    "SerializedSnapshot" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_WorkspaceContexts_RootPath_CreatedAt" ON "WorkspaceContexts" ("RootPath", "CreatedAt");
