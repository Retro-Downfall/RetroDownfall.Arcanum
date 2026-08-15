-- The core, content-free information-flow ledger. A row records that one artifact is Covenant
-- derived and where that taint came from, so reset, purge, and cache filters can find every tainted
-- sink without reopening the artifact. Only a tainted artifact carries a label: an untainted one has
-- no row at all, which is why exact provenance requires at least one generation rather than zero.
-- Session, Campaign, and turn are historical owner identities without foreign keys, because a label
-- outlives the turn that produced it and is retired through the owner-deletion journal.
CREATE TABLE IF NOT EXISTS artifact_sensitivity (
    LabelId TEXT NOT NULL PRIMARY KEY,
    ArtifactKindCode INTEGER NOT NULL CHECK (ArtifactKindCode IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13)),
    ArtifactId TEXT NOT NULL,
    SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (1)),
    ProvenanceModeCode INTEGER NOT NULL CHECK (ProvenanceModeCode IN (1, 2)),
    ExactGenerationIds BLOB NULL,
    GenerationBloom BLOB NULL,
    SessionId TEXT NULL,
    CampaignId TEXT NULL,
    TurnId TEXT NULL,
    ArtifactRevision INTEGER NOT NULL CHECK (ArtifactRevision >= 0),
    ArtifactContentDigest BLOB NOT NULL CHECK (length(ArtifactContentDigest) = 32),
    SensitivityDigest BLOB NOT NULL CHECK (length(SensitivityDigest) = 32),
    ProducingPlanDigest BLOB NULL CHECK (ProducingPlanDigest IS NULL OR length(ProducingPlanDigest) = 32),
    ProducingAdmissionDigest BLOB NULL CHECK (ProducingAdmissionDigest IS NULL OR length(ProducingAdmissionDigest) = 32),
    ProducingMaintenanceReceiptDigest BLOB NULL CHECK (ProducingMaintenanceReceiptDigest IS NULL OR length(ProducingMaintenanceReceiptDigest) = 32),
    ArtifactLabelDigest BLOB NOT NULL CHECK (length(ArtifactLabelDigest) = 32),
    CreatedAtUtc TEXT NOT NULL,
    -- Exact provenance packs 1 to 8 raw 16-byte generation identities; BloomOverflow is the fixed
    -- 256-bit bitset and nothing else. A row carrying both, neither, or a truncated vector would
    -- claim a provenance it cannot reproduce, and the sensitivity digest computed from it would
    -- never verify. An all-zero Bloom is refused because an overflow always has bits set.
    CHECK (
        (ProvenanceModeCode = 1
            AND GenerationBloom IS NULL
            AND ExactGenerationIds IS NOT NULL
            AND length(ExactGenerationIds) BETWEEN 16 AND 128
            AND length(ExactGenerationIds) % 16 = 0)
        OR (ProvenanceModeCode = 2
            AND ExactGenerationIds IS NULL
            AND GenerationBloom IS NOT NULL
            AND length(GenerationBloom) = 32
            AND GenerationBloom <> zeroblob(32))
    ),
    -- The exact vector is canonically sorted, and for raw big-endian identities that is the memcmp
    -- order SQLite already compares blobs in. Strictly increasing also proves the slots hold
    -- distinct generations, so a duplicate cannot pad the vector past the eight-generation ceiling
    -- and suppress the overflow switch.
    CHECK (
        ExactGenerationIds IS NULL
        OR (
            (length(ExactGenerationIds) < 32
                OR substr(ExactGenerationIds, 1, 16) < substr(ExactGenerationIds, 17, 16))
            AND (length(ExactGenerationIds) < 48
                OR substr(ExactGenerationIds, 17, 16) < substr(ExactGenerationIds, 33, 16))
            AND (length(ExactGenerationIds) < 64
                OR substr(ExactGenerationIds, 33, 16) < substr(ExactGenerationIds, 49, 16))
            AND (length(ExactGenerationIds) < 80
                OR substr(ExactGenerationIds, 49, 16) < substr(ExactGenerationIds, 65, 16))
            AND (length(ExactGenerationIds) < 96
                OR substr(ExactGenerationIds, 65, 16) < substr(ExactGenerationIds, 81, 16))
            AND (length(ExactGenerationIds) < 112
                OR substr(ExactGenerationIds, 81, 16) < substr(ExactGenerationIds, 97, 16))
            AND (length(ExactGenerationIds) < 128
                OR substr(ExactGenerationIds, 97, 16) < substr(ExactGenerationIds, 113, 16))
        )
    ),
    -- The label digest binds the plan and the admission it produced as a pair. One without the other
    -- would let a label claim a current Covenant admission it cannot show a plan for.
    CHECK (
        (ProducingPlanDigest IS NULL AND ProducingAdmissionDigest IS NULL)
        OR (ProducingPlanDigest IS NOT NULL AND ProducingAdmissionDigest IS NOT NULL)
    )
);

-- One live label per artifact. Two labels for the same artifact would let a purge remove one and
-- leave the artifact still evidenced as tainted by the other.
CREATE UNIQUE INDEX IF NOT EXISTS ux_artifact_sensitivity_artifact
    ON artifact_sensitivity(ArtifactKindCode, ArtifactId);

CREATE INDEX IF NOT EXISTS idx_artifact_sensitivity_session
    ON artifact_sensitivity(SessionId);

CREATE INDEX IF NOT EXISTS idx_artifact_sensitivity_campaign
    ON artifact_sensitivity(CampaignId);

CREATE INDEX IF NOT EXISTS idx_artifact_sensitivity_turn
    ON artifact_sensitivity(TurnId);
