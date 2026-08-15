-- The append-only core journal of protected effects that physically left this installation. It is
-- accounting, not content: a receipt says that something was disclosed to a destination class, never
-- what was disclosed. Storing raw content, a key, a path, or search text here would recreate the
-- exposure the journal exists to account for, and would survive local erasure because a receipt is
-- deliberately never cleared.
CREATE TABLE IF NOT EXISTS external_disclosure_receipts (
    -- Every row carries its origin installation, so a restored dataset cannot be mistaken for
    -- disclosures this installation made.
    OriginInstallationId TEXT NOT NULL CHECK (length(OriginInstallationId) > 0),
    -- CovenantDisclosureSubjectKind: Turn = 1, Operation = 2.
    SubjectKind INTEGER NOT NULL CHECK (SubjectKind IN (1, 2)),
    SubjectId TEXT NOT NULL CHECK (length(SubjectId) > 0),
    -- Allocated by disclosure_subject_state, which owns the checked counter. Ordinals start at one so
    -- that a zeroed last-allocated value means nothing has been allocated yet.
    SubjectOrdinal INTEGER NOT NULL CHECK (SubjectOrdinal > 0),
    -- ProviderDispatch = 1, McpToolUse = 2, WardEgress = 3, EncryptedBackup = 4,
    -- MaintenanceAttempt = 5. Closed so that an unknown category cannot be accounted for silently.
    EffectCategoryCode INTEGER NOT NULL CHECK (EffectCategoryCode IN (1, 2, 3, 4, 5)),
    CategoryPhysicalAttemptOrdinal INTEGER NOT NULL CHECK (CategoryPhysicalAttemptOrdinal > 0),
    -- Computed by the committer from the caller's frozen effect fields plus the assigned physical
    -- ordinal, so a caller cannot present a digest that makes a second dispatch look like the first.
    EffectIdentityDigest BLOB NOT NULL CHECK (length(EffectIdentityDigest) = 32),
    -- CovenantEgressDestination, one through eight.
    DestinationCode INTEGER NOT NULL CHECK (DestinationCode IN (1, 2, 3, 4, 5, 6, 7, 8)),
    -- CovenantDisclosureRevocability: LocallyRevocable = 1, Nonrevocable = 2.
    RevocabilityCode INTEGER NOT NULL CHECK (RevocabilityCode IN (1, 2)),
    -- A digest of the destination, never the destination itself: an endpoint, path, or recipient
    -- address is exactly the kind of content this journal must not retain.
    DestinationDigest BLOB NOT NULL CHECK (length(DestinationDigest) = 32),
    -- ContentSensitivity: None = 0, CovenantDerived = 1.
    SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (0, 1)),
    -- GenerationProvenanceMode: Exact = 1, BloomOverflow = 2.
    GenerationProvenanceModeCode INTEGER NOT NULL CHECK (GenerationProvenanceModeCode IN (1, 2)),
    -- One to eight canonically sorted 16-byte generation identities, concatenated.
    ExactGenerationIds BLOB NULL CHECK (
        ExactGenerationIds IS NULL
        OR (length(ExactGenerationIds) BETWEEN 16 AND 128 AND length(ExactGenerationIds) % 16 = 0)
    ),
    GenerationBloom BLOB NULL CHECK (GenerationBloom IS NULL OR length(GenerationBloom) = 32),
    WardEvidenceDigest BLOB NULL CHECK (WardEvidenceDigest IS NULL OR length(WardEvidenceDigest) = 32),
    AdmissionEvidenceDigest BLOB NULL CHECK (AdmissionEvidenceDigest IS NULL OR length(AdmissionEvidenceDigest) = 32),
    BackupEvidenceDigest BLOB NULL CHECK (BackupEvidenceDigest IS NULL OR length(BackupEvidenceDigest) = 32),
    DisclosedAtUtc TEXT NOT NULL,
    -- The subject ordinal is the identity. Two parallel calls cannot collide on it, because the
    -- committer allocates it inside the same transaction that inserts the row.
    PRIMARY KEY (OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal),
    -- The provenance aggregate is one shape or the other, never both and never neither. A row
    -- carrying both would let a reader pick whichever answer it preferred.
    CHECK (
        (GenerationProvenanceModeCode = 1 AND ExactGenerationIds IS NOT NULL AND GenerationBloom IS NULL)
        OR (GenerationProvenanceModeCode = 2 AND GenerationBloom IS NOT NULL AND ExactGenerationIds IS NULL)
    )
);

-- An exact effect is idempotent: a caller that proves it never dispatched may reuse the acknowledged
-- identity instead of manufacturing a second physical disclosure.
CREATE UNIQUE INDEX IF NOT EXISTS ux_external_disclosure_receipts_effect_identity
    ON external_disclosure_receipts(OriginInstallationId, SubjectKind, SubjectId, EffectIdentityDigest);

-- Distinct physical attempts within a category cannot reuse an ordinal, so the count of attempts
-- stays truthful even when a retry is uncertain about whether the first attempt left the process.
CREATE UNIQUE INDEX IF NOT EXISTS ux_external_disclosure_receipts_category_attempt
    ON external_disclosure_receipts(
        OriginInstallationId,
        SubjectKind,
        SubjectId,
        EffectCategoryCode,
        CategoryPhysicalAttemptOrdinal);

-- Compaction folds in contiguous subject-ordinal order, and status reads scan one subject's tail.
CREATE INDEX IF NOT EXISTS idx_external_disclosure_receipts_subject_ordinal
    ON external_disclosure_receipts(OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal);

-- Reset previews and audit reads group by destination class and revocability.
CREATE INDEX IF NOT EXISTS idx_external_disclosure_receipts_destination
    ON external_disclosure_receipts(DestinationCode, RevocabilityCode);

CREATE INDEX IF NOT EXISTS idx_external_disclosure_receipts_disclosed_at
    ON external_disclosure_receipts(DisclosedAtUtc);
