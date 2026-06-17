BEGIN TRANSACTION;
ALTER TABLE "Conversations" ADD "LastSummarizedMessageAt" TEXT NULL;

ALTER TABLE "Conversations" ADD "Summary" TEXT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260509005818_AddCampaignLogFields', '10.0.8');

COMMIT;

