-- The durable recovery owner for a protected fork or selective import. A transfer stages attachment
-- blobs on the filesystem and then commits Session rows in the database, and a crash can land
-- anywhere between those two. Recovery cannot infer the owner from the half-built result, so the
-- complete CovenantExclusiveRecoveryOwner for ProtectedSessionTransfer is committed here first and
-- every later step compares against it.
CREATE TABLE IF NOT EXISTS protected_session_transfer_intents (
    OperationId TEXT NOT NULL PRIMARY KEY CHECK (length(OperationId) > 0),
    -- The exact Arcanum.Covenant.ProtectedSessionTransfer.v1 effect digest.
    EffectDigest BLOB NOT NULL CHECK (length(EffectDigest) = 32),
    SourceEvidenceDigest BLOB NOT NULL CHECK (length(SourceEvidenceDigest) = 32),
    DestinationBindingDigest BLOB NOT NULL CHECK (length(DestinationBindingDigest) = 32),
    -- CovenantScope: Global = 1, Campaign = 2.
    DestinationScopeCode INTEGER NOT NULL CHECK (DestinationScopeCode IN (1, 2)),
    -- Historical: recorded without a foreign key so a destination Campaign deleted mid-recovery
    -- cannot block the journal from being resolved.
    DestinationCampaignId TEXT NULL,
    DestinationSessionId TEXT NOT NULL CHECK (length(DestinationSessionId) > 0),
    -- Covers the exact ordered child preimages, so a missing or reordered blob is detectable without
    -- reading the blobs themselves.
    AttachmentManifestDigest BLOB NOT NULL CHECK (length(AttachmentManifestDigest) = 32),
    AttachmentManifestCount INTEGER NOT NULL CHECK (AttachmentManifestCount >= 0),
    -- Encrypted durable destination-root identity. No live handle and no source capability: a
    -- retained handle cannot survive a crash, and persisting a source lease would let recovery
    -- resume reading a source it no longer holds.
    DestinationRootIdentityEvidence BLOB NOT NULL CHECK (
        length(DestinationRootIdentityEvidence) BETWEEN 1 AND 8192
    ),
    -- Prepared = 1, BlobsStaged = 2, DatabaseCommitted = 3, ReopenPending = 4, Completed = 5,
    -- Abandoned = 6.
    PhaseCode INTEGER NOT NULL CHECK (PhaseCode IN (1, 2, 3, 4, 5, 6)),
    -- CovenantExclusiveLeaseDisposition: RollbackAndReopen = 1, CommitAndReopen = 2. KeepClosed is
    -- never stored, because it means the operation stayed at its last proven phase rather than
    -- reaching a disposition at all.
    PendingDispositionCode INTEGER NULL CHECK (PendingDispositionCode IS NULL OR PendingDispositionCode IN (1, 2)),
    Revision INTEGER NOT NULL CHECK (Revision >= 0),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    -- A global destination has no Campaign and a Campaign destination must name one, otherwise
    -- recovery cannot tell which scope to reopen.
    CHECK (
        (DestinationScopeCode = 1 AND DestinationCampaignId IS NULL)
        OR (DestinationScopeCode = 2 AND DestinationCampaignId IS NOT NULL)
    ),
    -- A disposition is a record of what the gate was asked to do. Before ReopenPending nothing has
    -- been asked, so a stored code there would let a finalizer act on a decision nobody made.
    CHECK (PhaseCode >= 4 OR PendingDispositionCode IS NULL),
    CHECK (PhaseCode < 4 OR PendingDispositionCode IS NOT NULL)
);

-- Recovery enumerates every nonterminal transfer before admission reopens.
CREATE INDEX IF NOT EXISTS idx_protected_session_transfer_intents_phase
    ON protected_session_transfer_intents(PhaseCode);

CREATE INDEX IF NOT EXISTS idx_protected_session_transfer_intents_destination_session
    ON protected_session_transfer_intents(DestinationSessionId);
