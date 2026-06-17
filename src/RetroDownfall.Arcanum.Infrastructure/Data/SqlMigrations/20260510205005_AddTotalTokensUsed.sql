BEGIN TRANSACTION;
ALTER TABLE "Conversations" ADD "TotalTokensUsed" INTEGER NOT NULL DEFAULT 0;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260510205005_AddTotalTokensUsed', '10.0.8');

COMMIT;

