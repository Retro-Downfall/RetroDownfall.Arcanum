CREATE TABLE IF NOT EXISTS grimoire_feature_schemas (
    FamilyCode INTEGER NOT NULL,
    TransactionTierCode INTEGER NOT NULL,
    SchemaVersion INTEGER NOT NULL CHECK (SchemaVersion > 0),
    SourceDefinitionFingerprint TEXT NOT NULL CHECK (length(SourceDefinitionFingerprint) = 64),
    InstalledCatalogFingerprint TEXT NOT NULL CHECK (length(InstalledCatalogFingerprint) = 71),
    InstalledAtUtc TEXT NOT NULL,
    HealthCode INTEGER NOT NULL,
    HealthDetailCode TEXT NULL,
    PRIMARY KEY (FamilyCode, TransactionTierCode)
);

CREATE INDEX IF NOT EXISTS idx_grimoire_feature_schemas_health
    ON grimoire_feature_schemas (HealthCode, FamilyCode, TransactionTierCode);
