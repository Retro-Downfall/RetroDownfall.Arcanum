CREATE TABLE IF NOT EXISTS campaign_path_marker_intents (
    -- The intent has its own random identity, separate from the owner operation, because one restore
    -- or full-reset owner journals a distinct row for every Campaign it must clean up. Reusing the
    -- owner ID as the key would collapse those rows into one and lose every Campaign after the
    -- first.
    IntentId TEXT NOT NULL PRIMARY KEY CHECK (length(IntentId) > 0),
    OwnerOperationId TEXT NOT NULL CHECK (length(OwnerOperationId) > 0),
    -- Historical, and deliberately without a foreign key: a CampaignDelete intent outlives the
    -- Campaign row it cleans up after, and core owner deletion must never wait on workspace state.
    CampaignId TEXT NOT NULL,
    -- PathMutation = 1, CampaignDelete = 2, RestoreCleanup = 3, FullInstallationResetCleanup = 4.
    IntentKindCode INTEGER NOT NULL CHECK (IntentKindCode IN (1, 2, 3, 4)),
    -- The exclusive gate owner that authorizes this kind: CampaignPathMutation = 1,
    -- CampaignDelete = 2, BackupRestore = 5.
    ExclusiveOwnerOperationCode INTEGER NULL CHECK (ExclusiveOwnerOperationCode IS NULL OR ExclusiveOwnerOperationCode IN (1, 2, 5)),
    OwnerEffectDigest BLOB NOT NULL CHECK (length(OwnerEffectDigest) = 32),
    -- The marker bytes, encrypted at rest by the database. Both this and the temporary-name
    -- capability are securely cleared once the effect is proven, which is why they are nullable
    -- while their digests are not.
    EncryptedMarkerPayload BLOB NULL CHECK (EncryptedMarkerPayload IS NULL OR length(EncryptedMarkerPayload) BETWEEN 1 AND 4096),
    MarkerDigest BLOB NOT NULL CHECK (length(MarkerDigest) = 32),
    -- Only a PathMutation answers a public receipt, so only it carries the receipt-first request
    -- digest. Cleanup kinds authenticate from their owning journal instead, and must never be able
    -- to substitute an effect digest into this column and appear to answer a request nobody made.
    ApplyRequestDigest BLOB NULL CHECK (ApplyRequestDigest IS NULL OR length(ApplyRequestDigest) = 32),
    TemporaryBaseName TEXT NULL CHECK (TemporaryBaseName IS NULL OR length(TemporaryBaseName) BETWEEN 1 AND 255),
    -- Observed once from the same newly opened temporary-file handle, so recovery can prove the
    -- rename moved that exact object rather than whatever now answers to the temporary name.
    TemporaryPhysicalIdentityDigest BLOB NULL CHECK (TemporaryPhysicalIdentityDigest IS NULL OR length(TemporaryPhysicalIdentityDigest) = 32),
    -- Nullable for kind four only. A full-reset child whose Campaign vanished between inventory and
    -- the pair effect still has to be journaled, and manufacturing a path for it would hand
    -- reconciliation a location nobody observed. Every other kind still requires one.
    TargetDisplayPath TEXT NULL CHECK (TargetDisplayPath IS NULL OR length(TargetDisplayPath) BETWEEN 1 AND 4096),
    -- Zero is legal: a first registration replaces no earlier identity revision.
    PriorRevision INTEGER NOT NULL CHECK (PriorRevision >= 0),
    -- Opened = 1, Absent = 2. Both this and the reopened identity are filled exactly once on entry
    -- to TargetReopenedOrAbsent and preserved afterwards, so the proof survives the secure
    -- destruction of the marker payload.
    TargetObservationCode INTEGER NULL CHECK (TargetObservationCode IS NULL OR TargetObservationCode IN (1, 2)),
    ReopenedTargetPhysicalIdentityDigest BLOB NULL CHECK (ReopenedTargetPhysicalIdentityDigest IS NULL OR length(ReopenedTargetPhysicalIdentityDigest) = 32),
    -- RollbackAndReopen = 1, CommitAndReopen = 2. Uncertainty keeps the scope closed and records
    -- nothing here, so this column is never a guess about what the filesystem did.
    PendingDispositionCode INTEGER NULL CHECK (PendingDispositionCode IS NULL OR PendingDispositionCode IN (1, 2)),
    -- Prepared = 1, TempCreated = 2, TempWritten = 3, TempFsynced = 4, RenamedNoReplace = 5,
    -- ParentFsynced = 6, TargetReopenedOrAbsent = 7, CodecOrAbsenceVerified = 8,
    -- DatabaseStateCommitted = 9, SensitiveMaterialDestroyed = 10, ReopenPending = 11,
    -- Completed = 12, Compensated = 13, ManualBlocker = 14, OrphanReopenPending = 15,
    -- Orphaned = 16.
    PhaseCode INTEGER NOT NULL CHECK (PhaseCode BETWEEN 1 AND 16),
    -- The compare-and-swap counter every authorized phase transition advances by exactly one, so two
    -- recovery attempts cannot both believe they moved the same intent.
    PhaseRevision INTEGER NOT NULL CHECK (PhaseRevision > 0),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    -- The intent kind and its gate owner are one decision, not two. Kind 4 alone has no in-process
    -- owner: its authority is the stopped-host installation lock plus the authenticated reset
    -- journal, and letting it name a gate owner would invite ordinary recovery to adopt it.
    CHECK (
        (IntentKindCode = 1 AND ExclusiveOwnerOperationCode = 1)
        OR (IntentKindCode = 2 AND ExclusiveOwnerOperationCode = 2)
        OR (IntentKindCode = 3 AND ExclusiveOwnerOperationCode = 5)
        OR (IntentKindCode = 4 AND ExclusiveOwnerOperationCode IS NULL)
    ),
    CHECK (
        (IntentKindCode = 1 AND ApplyRequestDigest IS NOT NULL)
        OR (IntentKindCode <> 1 AND ApplyRequestDigest IS NULL)
    ),
    -- The payload and the temporary-name capability are committed together and cleared together by
    -- one authorized transition. Half a pair would leave a usable capability behind after the
    -- material it names was destroyed.
    CHECK (
        (EncryptedMarkerPayload IS NULL AND TemporaryBaseName IS NULL)
        OR (EncryptedMarkerPayload IS NOT NULL AND TemporaryBaseName IS NOT NULL)
    ),
    -- An opened target has exactly one observed identity; a proven absence has none. Allowing an
    -- identity beside Absent, or none beside Opened, would let a later comparison pass against
    -- evidence that was never taken from a real handle.
    CHECK (
        (TargetObservationCode IS NULL AND ReopenedTargetPhysicalIdentityDigest IS NULL)
        OR (TargetObservationCode = 1 AND ReopenedTargetPhysicalIdentityDigest IS NOT NULL)
        OR (TargetObservationCode = 2 AND ReopenedTargetPhysicalIdentityDigest IS NULL)
    ),
    -- A disposition exists only while one is pending. Outside those two phases it would be an
    -- instruction to a gate that has already been told what to do.
    CHECK (PhaseCode IN (11, 15) OR PendingDispositionCode IS NULL),
    -- Campaign deletion cannot be rolled back once the core owner delete commits, so its only legal
    -- disposition is CommitAndReopen. Kind 4 has no gate to dispose at all.
    CHECK (IntentKindCode <> 2 OR PendingDispositionCode IS NULL OR PendingDispositionCode = 2),
    CHECK (IntentKindCode <> 4 OR PendingDispositionCode IS NULL),
    -- The orphan arm exists only because core Campaign deletion cannot stay blocked by a workspace
    -- it no longer owns. No other kind has that problem, and none may borrow the arm to abandon work
    -- it is still able to finish.
    CHECK (PhaseCode NOT IN (15, 16) OR IntentKindCode = 2),
    -- Only kind four may omit the target display path.
    CHECK (IntentKindCode = 4 OR TargetDisplayPath IS NOT NULL),
    -- A full installation reset cleans up an already registered Campaign, so there is always an
    -- earlier identity revision behind it. Zero would be a first registration, which this kind never
    -- performs.
    CHECK (IntentKindCode <> 4 OR PriorRevision > 0),
    -- Kind four carries no marker payload of its own to destroy, no temporary name to rename
    -- through, and no one-time target observation: its evidence lives in the companion row, written
    -- once before either host-tools marker was touched. Any of these columns set here would be
    -- authority the reset never received.
    CHECK (
        IntentKindCode <> 4
        OR (EncryptedMarkerPayload IS NULL
            AND TemporaryBaseName IS NULL
            AND TemporaryPhysicalIdentityDigest IS NULL
            AND TargetObservationCode IS NULL
            AND ReopenedTargetPhysicalIdentityDigest IS NULL)
    ),
    -- Prepared, Completed, or ManualBlocker. The two-phase filesystem phases belong to a kind that
    -- writes a marker; kind four only deletes one it already proved is the expected one.
    CHECK (IntentKindCode <> 4 OR PhaseCode IN (1, 12, 14))
);

-- One active intent per historical Campaign. Terminal rows are excluded so completed, compensated,
-- and orphaned evidence can accumulate, while a second live two-phase operation against the same
-- Campaign root is refused rather than racing the first.
CREATE UNIQUE INDEX IF NOT EXISTS ux_campaign_path_marker_intents_active_campaign
    ON campaign_path_marker_intents(CampaignId) WHERE PhaseCode NOT IN (12, 13, 16);

-- The replay key. A retried owner reaches the row it already journaled instead of creating a second
-- one, and one restore or full-reset owner still journals a distinct row per Campaign and kind.
CREATE UNIQUE INDEX IF NOT EXISTS ux_campaign_path_marker_intents_owner_campaign_kind
    ON campaign_path_marker_intents(OwnerOperationId, CampaignId, IntentKindCode);

-- Startup recovery selects adoptable work by kind and phase before readiness, and must not read the
-- whole journal to find that an installation has none.
CREATE INDEX IF NOT EXISTS idx_campaign_path_marker_intents_kind_phase
    ON campaign_path_marker_intents(IntentKindCode, PhaseCode);
