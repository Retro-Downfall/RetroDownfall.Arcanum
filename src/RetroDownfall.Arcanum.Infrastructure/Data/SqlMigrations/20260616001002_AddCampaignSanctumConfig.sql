BEGIN TRANSACTION;
ALTER TABLE "Campaigns" ADD "SanctumConfigJson" TEXT NOT NULL DEFAULT '{}';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260616001002_AddCampaignSanctumConfig', '10.0.8');

COMMIT;

