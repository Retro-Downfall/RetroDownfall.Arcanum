-- Both the crash journal and the durable ownership catalog for one managed workspace file. Creating
-- a file and labelling it are two effects in two different systems, and a crash between them would
-- otherwise leave an unlabelled sensitive file that nothing in the database admits to owning. The
-- row is written before the first filesystem byte, carries a complete label projection so the label
-- can still be created after a restart, and keeps the physical identity of the child it created so
-- that a same-content file swapped in underneath can never be adopted.
CREATE TABLE IF NOT EXISTS managed_file_write_intents (
    WriteOperationId TEXT NOT NULL PRIMARY KEY CHECK (length(WriteOperationId) > 0),
    StableEffectIdentityDigest BLOB NOT NULL CHECK (length(StableEffectIdentityDigest) = 32),
    ArtifactId TEXT NOT NULL CHECK (length(ArtifactId) > 0),
    SensitivityLabelId TEXT NOT NULL CHECK (length(SensitivityLabelId) > 0),
    SensitivityLabelDigest BLOB NOT NULL CHECK (length(SensitivityLabelDigest) = 32),
    -- The complete encrypted ArtifactSensitivityLabel projection. Required and byte-for-byte
    -- immutable through ParentFsynced, then securely cleared by the same transaction that
    -- terminalizes the row, because after that point only the content-free label identity and digest
    -- are needed and retaining the projection would keep sensitive fields alive indefinitely.
    PendingArtifactSensitivityLabel BLOB NULL CHECK (
        PendingArtifactSensitivityLabel IS NULL
        OR length(PendingArtifactSensitivityLabel) BETWEEN 1 AND 16384
    ),
    -- Encrypted ManagedFileWriteDurableLocationEvidence: canonical Campaign root identity digest,
    -- positive path revision, bounded normalized relative parent segments, same-handle parent
    -- physical identity, bounded target leaf, and one distinct bounded random temporary leaf under
    -- that exact parent.
    DurableLocationEvidence BLOB NOT NULL CHECK (length(DurableLocationEvidence) BETWEEN 1 AND 8192),
    ExpectedContentHash BLOB NOT NULL CHECK (length(ExpectedContentHash) = 32),
    ExpectedContentLength INTEGER NOT NULL CHECK (ExpectedContentLength >= 0),
    -- Observed once from the same newly created and still-open temporary handle, before the first
    -- byte is written. It is the only thing that proves a later child was created by this operation.
    CreatedChildPhysicalIdentityDigest BLOB NULL CHECK (
        CreatedChildPhysicalIdentityDigest IS NULL
        OR length(CreatedChildPhysicalIdentityDigest) = 32
    ),
    -- Encrypted ManagedFileOwnershipEvidence for the reopened final file.
    FinalOwnershipEvidence BLOB NULL CHECK (
        FinalOwnershipEvidence IS NULL
        OR length(FinalOwnershipEvidence) BETWEEN 1 AND 4096
    ),
    -- ManagedFileWriteIntentPhase: Prepared = 1, TempCreated = 2, TempWritten = 3, TempFsynced = 4,
    -- RenamedNoReplace = 5, ParentFsynced = 6, AdoptedAndLabeled = 7, Cleaned = 8,
    -- ManualNonrevocable = 9, Erased = 10.
    PhaseCode INTEGER NOT NULL CHECK (PhaseCode IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10)),
    Revision INTEGER NOT NULL CHECK (Revision >= 0),
    RetryCount INTEGER NOT NULL CHECK (RetryCount >= 0),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    -- The projection exists exactly while the label has not been created yet. A terminal row holding
    -- it would keep the sensitive content that adoption was supposed to consume.
    CHECK (
        (PhaseCode BETWEEN 1 AND 6 AND PendingArtifactSensitivityLabel IS NOT NULL)
        OR (PhaseCode BETWEEN 7 AND 10 AND PendingArtifactSensitivityLabel IS NULL)
    ),
    -- Ownership evidence exists exactly for the two phases that own a real adopted file. Cleaned and
    -- ManualNonrevocable never adopted anything, so evidence there would assert ownership of a file
    -- this operation could not authenticate.
    CHECK (
        (PhaseCode IN (7, 10) AND FinalOwnershipEvidence IS NOT NULL)
        OR (PhaseCode NOT IN (7, 10) AND FinalOwnershipEvidence IS NULL)
    ),
    -- Prepared has not created a child yet. Every phase that acted on a child requires the identity
    -- of the child it created. Cleaned and ManualNonrevocable accept either, because they are also
    -- the two terminal shapes a Prepared row reaches when recovery proves both children absent or
    -- cannot authenticate a child that already exists.
    CHECK (
        (PhaseCode = 1 AND CreatedChildPhysicalIdentityDigest IS NULL)
        OR (PhaseCode IN (2, 3, 4, 5, 6, 7, 10) AND CreatedChildPhysicalIdentityDigest IS NOT NULL)
        OR PhaseCode IN (8, 9)
    )
);

-- One durable row per stable effect, so a retried write request resumes the existing journal instead
-- of creating a second operation that would create a second file.
CREATE UNIQUE INDEX IF NOT EXISTS ux_managed_file_write_intents_effect
    ON managed_file_write_intents(StableEffectIdentityDigest);

-- Erasure, restore staging, and retention all reach this table through the artifact and its label.
CREATE INDEX IF NOT EXISTS idx_managed_file_write_intents_artifact
    ON managed_file_write_intents(ArtifactId);

CREATE INDEX IF NOT EXISTS idx_managed_file_write_intents_label
    ON managed_file_write_intents(SensitivityLabelId);

-- Restart recovery enumerates every nonterminal phase before the writer runs again.
CREATE INDEX IF NOT EXISTS idx_managed_file_write_intents_phase
    ON managed_file_write_intents(PhaseCode);
