BEGIN TRANSACTION;
DROP INDEX "IX_WorkspaceContexts_RootPath";

ALTER TABLE "WorkspaceContexts" RENAME COLUMN "ProjectSummary" TO "SerializedSnapshot";

ALTER TABLE "WorkspaceContexts" RENAME COLUMN "LastScanned" TO "CreatedAt";

CREATE INDEX "IX_WorkspaceContexts_RootPath_CreatedAt" ON "WorkspaceContexts" ("RootPath", "CreatedAt");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260509195722_EvolveWorkspaceContextForChronosync', '10.0.8');

COMMIT;

