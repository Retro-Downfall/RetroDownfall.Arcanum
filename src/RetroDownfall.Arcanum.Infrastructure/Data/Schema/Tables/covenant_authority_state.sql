CREATE TABLE IF NOT EXISTS covenant_authority_state (
    -- Fixed to 1: one installation has exactly one authority identity, and a key that can only hold
    -- one value says so without a trigger. This row is core rather than Covenant-owned so optional
    -- Covenant schema damage, a restart, or a key rotation cannot erase the evidence it carries.
    StateKey INTEGER NOT NULL PRIMARY KEY CHECK (StateKey = 1),
    InstallationIdentity TEXT NOT NULL CHECK (length(InstallationIdentity) BETWEEN 1 AND 128),
    AuthorityEpoch INTEGER NOT NULL CHECK (AuthorityEpoch > 0),
    -- The master key version is unsigned in the domain and is kept in checked signed storage because
    -- SQLite has no unsigned integer. The check is what makes the storage narrowing safe: a negative
    -- value read back as unsigned would appear to be an enormous future version and would make a
    -- rotate-back look like an advance.
    CurrentMasterKeyVersion INTEGER NOT NULL CHECK (CurrentMasterKeyVersion > 0),
    CurrentMasterKeyFingerprint BLOB NOT NULL CHECK (length(CurrentMasterKeyFingerprint) = 32),
    RecoveryEnvelopeEpoch INTEGER NOT NULL CHECK (RecoveryEnvelopeEpoch > 0),
    -- Clean = 1, PendingHostToolsTaint = 2, HostToolsTainted = 3.
    HostToolsStateCode INTEGER NOT NULL CHECK (HostToolsStateCode IN (1, 2, 3)),
    -- The master version in force when the taint was recorded. It stays put across every later key
    -- rotation, because the question it answers is which credentials same-identity code could have
    -- recovered, and a rotation does not unask it.
    TaintTimeMasterVersion INTEGER NULL CHECK (TaintTimeMasterVersion IS NULL OR TaintTimeMasterVersion > 0),
    TaintFingerprint BLOB NULL CHECK (TaintFingerprint IS NULL OR length(TaintFingerprint) = 32),
    -- One random uppercase hyphenated identity per taint transition, so two separate escapes cannot
    -- be collapsed into one remediation record.
    TransitionId TEXT NULL CHECK (TransitionId IS NULL OR length(TransitionId) = 36),
    UpdatedAtUtc TEXT NOT NULL,
    -- A clean installation carries no taint evidence at all, and a pending or tainted one cannot
    -- exist without the transition identity and taint-time version that make it actionable. Encoding
    -- the shape here means no startup path, rotation, or reinitialize can quietly produce a row that
    -- claims to be clean while holding taint fields, or claims a taint it cannot describe.
    CHECK (
        (HostToolsStateCode = 1
            AND TaintTimeMasterVersion IS NULL
            AND TaintFingerprint IS NULL
            AND TransitionId IS NULL)
        OR (HostToolsStateCode IN (2, 3)
            AND TaintTimeMasterVersion IS NOT NULL
            AND TransitionId IS NOT NULL)
    )
);
