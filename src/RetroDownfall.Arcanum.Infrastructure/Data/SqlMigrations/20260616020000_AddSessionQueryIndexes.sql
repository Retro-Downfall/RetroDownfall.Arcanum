BEGIN TRANSACTION;

CREATE INDEX "IX_Sessions_CampaignId" ON "Sessions" ("CampaignId");

CREATE INDEX "IX_Sessions_Status_UpdatedAt" ON "Sessions" ("Status", "UpdatedAt");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260616020000_AddSessionQueryIndexes', '10.0.8');

COMMIT;
