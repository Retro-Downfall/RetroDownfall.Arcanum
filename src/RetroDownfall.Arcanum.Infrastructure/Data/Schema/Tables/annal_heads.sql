-- The guarded current pointer. A head is meant to move; what it must never do is move backwards or
-- change what it is a pointer to, which annal_heads_validate_update enforces.
CREATE TABLE IF NOT EXISTS annal_heads (
    ClaimId TEXT NOT NULL PRIMARY KEY,
    SubjectStoreCode INTEGER NOT NULL CHECK (SubjectStoreCode IN (1, 2)),
    CurrentVersionId TEXT NOT NULL,
    CurrentRevision INTEGER NOT NULL CHECK (CurrentRevision > 0),
    CurrentOperationCode INTEGER NOT NULL CHECK (CurrentOperationCode IN (1, 2, 3)),
    UpdatedAtUtc TEXT NOT NULL,
    -- Both references are composite, and that is the point. A plain reference to VersionId would let a
    -- head adopt a version belonging to another claim, or one whose revision and operation disagree with
    -- the head's own columns; a plain reference to ClaimId would let a head claim a store its own claim
    -- does not belong to, and a store-scoped erasure would then walk past it.
    FOREIGN KEY (CurrentVersionId, ClaimId, CurrentRevision, CurrentOperationCode)
        REFERENCES annal_versions(VersionId, ClaimId, Revision, OperationCode),
    FOREIGN KEY (ClaimId, SubjectStoreCode) REFERENCES annal_claims(ClaimId, SubjectStoreCode)
);

-- One version is current for at most one claim.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_heads_current_version
ON annal_heads(CurrentVersionId);

-- A store-scoped erasure reads this to find the heads it must release before the versions may go.
CREATE INDEX IF NOT EXISTS idx_annal_heads_store
ON annal_heads(SubjectStoreCode);
