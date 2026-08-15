-- The always-present core journal for repairing a schema family. Repair mutates the catalog itself,
-- so recovery cannot reconstruct who owned the operation by inspecting the repaired result: a
-- half-installed family looks the same whether this process created it or found it. The row is
-- committed before the first repair DDL and carries the complete CovenantExclusiveRecoveryOwner for
-- SchemaRepair, which is the only thing that lets restart recovery reopen admission through the
-- matching disposition instead of guessing.
CREATE TABLE IF NOT EXISTS covenant_schema_repair_intents (
    OperationId TEXT NOT NULL PRIMARY KEY CHECK (length(OperationId) > 0),
    EffectDigest BLOB NOT NULL CHECK (length(EffectDigest) = 32),
    -- The whole-catalog digest observed before any mutation. A catalog that no longer matches it
    -- means someone else changed the schema, and this journal no longer describes reality.
    InspectedCatalogDigest BLOB NOT NULL CHECK (length(InspectedCatalogDigest) = 32),
    -- InstallAbsentCanonicalFamily = 1, RepairExistingFamily = 2, RepairOrdinaryIndex = 3.
    RepairActionCode INTEGER NOT NULL CHECK (RepairActionCode IN (1, 2, 3)),
    -- GrimoireSchemaTransactionTier: Core = 0, CovenantCanonical = 1, CovenantAccelerator = 2.
    TargetTierCode INTEGER NOT NULL CHECK (TargetTierCode IN (0, 1, 2)),
    -- The 128-bit dataset generation captured before the repair. A newly installed generation is
    -- recorded separately in post-commit health evidence and never rewrites this field, so the
    -- journal keeps saying what the repair started from.
    CapturedDatasetGeneration BLOB NULL CHECK (
        CapturedDatasetGeneration IS NULL OR length(CapturedDatasetGeneration) = 16
    ),
    AuthorityEpoch INTEGER NOT NULL CHECK (AuthorityEpoch > 0),
    -- Prepared = 1, CatalogCommitted = 2, HealthVerified = 3, ReopenPending = 4, Completed = 5,
    -- Abandoned = 6.
    PhaseCode INTEGER NOT NULL CHECK (PhaseCode IN (1, 2, 3, 4, 5, 6)),
    Revision INTEGER NOT NULL CHECK (Revision >= 0),
    -- A bounded code, never an exception message: an unbounded error string in a core journal is
    -- both an unbounded row and a place for content to leak.
    LastDurableErrorCode TEXT NULL CHECK (
        LastDurableErrorCode IS NULL OR length(LastDurableErrorCode) BETWEEN 1 AND 64
    ),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    -- There is no generation to capture only when the canonical family is wholly absent. Any other
    -- action repairs something that already exists and must record what it was.
    CHECK (
        (RepairActionCode = 1 AND CapturedDatasetGeneration IS NULL)
        OR (RepairActionCode IN (2, 3) AND CapturedDatasetGeneration IS NOT NULL)
    )
);

-- Restart recovery selects the nonterminal intent before readiness.
CREATE INDEX IF NOT EXISTS idx_covenant_schema_repair_intents_phase
    ON covenant_schema_repair_intents(PhaseCode);
