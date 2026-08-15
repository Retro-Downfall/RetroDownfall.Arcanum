CREATE TABLE IF NOT EXISTS owner_deletion_events (
    -- The reusable core deletion journal. It is always present, even when no capability is
    -- installed, so core Campaign and Session deletion never depends on an optional tier being
    -- healthy. Each installed capability tracks its own applied sequence and consumes events at its
    -- own pace.
    Sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (Sequence > 0),
    OwnerKindCode INTEGER NOT NULL CHECK (OwnerKindCode IN (1, 2)),
    OwnerId TEXT NOT NULL,
    OperationId TEXT NULL,
    ExclusiveEffectDigest BLOB NULL CHECK (ExclusiveEffectDigest IS NULL OR length(ExclusiveEffectDigest) = 32),
    DeletedAtUtc TEXT NOT NULL,
    -- A managed deletion copies both fields from its prepared intent; an unmanaged one has neither.
    -- No trigger may invent an effect digest, so the pair is present or absent together.
    CHECK (
        (OperationId IS NULL AND ExclusiveEffectDigest IS NULL)
        OR (OperationId IS NOT NULL AND ExclusiveEffectDigest IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS idx_owner_deletion_events_kind_sequence
    ON owner_deletion_events(OwnerKindCode, Sequence);

CREATE INDEX IF NOT EXISTS idx_owner_deletion_events_kind_owner_sequence
    ON owner_deletion_events(OwnerKindCode, OwnerId, Sequence);
