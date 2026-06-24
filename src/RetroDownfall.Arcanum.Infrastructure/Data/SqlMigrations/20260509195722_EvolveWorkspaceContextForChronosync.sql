DROP INDEX IF EXISTS "IX_WorkspaceContexts_RootPath";

ALTER TABLE "WorkspaceContexts" RENAME COLUMN "ProjectSummary" TO "SerializedSnapshot";

ALTER TABLE "WorkspaceContexts" RENAME COLUMN "LastScanned" TO "CreatedAt";

CREATE INDEX IF NOT EXISTS "IX_WorkspaceContexts_RootPath_CreatedAt" ON "WorkspaceContexts" ("RootPath", "CreatedAt");
