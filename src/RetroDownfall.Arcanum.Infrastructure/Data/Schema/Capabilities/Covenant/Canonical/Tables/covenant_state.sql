CREATE TABLE IF NOT EXISTS covenant_state (
    -- Fixed to 1: this table is a singleton, and a key that can only hold one value is the cheapest
    -- way to say so in SQLite without a trigger.
    StateKey INTEGER NOT NULL PRIMARY KEY CHECK (StateKey = 1),
    DatasetGeneration BLOB NOT NULL CHECK (length(DatasetGeneration) = 16),
    CanonicalSearchSequence INTEGER NOT NULL CHECK (CanonicalSearchSequence >= 0),
    AppliedDatasetGeneration BLOB NULL CHECK (AppliedDatasetGeneration IS NULL OR length(AppliedDatasetGeneration) = 16),
    AppliedSearchSequence INTEGER NULL CHECK (AppliedSearchSequence IS NULL OR AppliedSearchSequence >= 0),
    AppliedCampaignDeletionSequence INTEGER NOT NULL CHECK (AppliedCampaignDeletionSequence >= 0),
    AppliedSessionDeletionSequence INTEGER NOT NULL CHECK (AppliedSessionDeletionSequence >= 0),
    AcceleratorEpoch INTEGER NOT NULL CHECK (AcceleratorEpoch > 0),
    KeyReclamationEpoch INTEGER NOT NULL CHECK (KeyReclamationEpoch > 0),
    EnvelopeMasterKeyVersion INTEGER NOT NULL CHECK (EnvelopeMasterKeyVersion > 0),
    EnvelopeMasterKeyFingerprint BLOB NOT NULL CHECK (length(EnvelopeMasterKeyFingerprint) = 32),
    EnvelopeKeyEpoch INTEGER NOT NULL CHECK (EnvelopeKeyEpoch > 0),
    NextSearchRowId INTEGER NOT NULL CHECK (NextSearchRowId > 0),
    RebuildStateCode INTEGER NOT NULL CHECK (RebuildStateCode IN (1, 2, 3)),
    RebuildTargetSequence INTEGER NULL CHECK (RebuildTargetSequence IS NULL OR RebuildTargetSequence >= 0),
    RebuildCursor INTEGER NULL CHECK (RebuildCursor IS NULL OR RebuildCursor >= 0),
    UpdatedAtUtc TEXT NOT NULL,
    -- The applied FTS tuple is all-or-nothing. Half a tuple would let a stale generation pass an
    -- equality check against a fresh sequence and serve text from a reset dataset.
    CHECK (
        (AppliedDatasetGeneration IS NULL AND AppliedSearchSequence IS NULL)
        OR (AppliedDatasetGeneration IS NOT NULL AND AppliedSearchSequence IS NOT NULL)
    ),
    -- Only a rebuild in progress has a captured target and cursor.
    CHECK (
        (RebuildStateCode = 3 AND RebuildTargetSequence IS NOT NULL)
        OR (RebuildStateCode <> 3 AND RebuildTargetSequence IS NULL AND RebuildCursor IS NULL)
    )
);
