-- The conservative current projection of a Session's information flow, one row per Session. The
-- inference response-cache filter reads exactly this row in one indexed lookup after request
-- binding and before any cache probe, so a previously tainted Session cannot replay a cached answer
-- while Covenant is disabled, and an untainted Session does not pay one query per message. It is
-- maintained atomically with the Session-owned artifact_sensitivity rows it summarizes.
CREATE TABLE IF NOT EXISTS session_sensitivity_state (
    SessionId TEXT NOT NULL PRIMARY KEY REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    TaintedArtifactCount INTEGER NOT NULL CHECK (TaintedArtifactCount >= 0),
    MaximumSensitivityCode INTEGER NOT NULL CHECK (MaximumSensitivityCode IN (0, 1)),
    GenerationProvenanceDigest BLOB NOT NULL CHECK (length(GenerationProvenanceDigest) = 32),
    Revision INTEGER NOT NULL CHECK (Revision >= 0),
    UpdatedAtUtc TEXT NOT NULL,
    -- The projection is conservative in one direction only. It may report a maximum the live
    -- artifacts no longer justify, because taint that has been purged still bars a cache replay,
    -- but it must never report None while tainted artifacts are still counted.
    CHECK (TaintedArtifactCount = 0 OR MaximumSensitivityCode = 1)
);

CREATE INDEX IF NOT EXISTS idx_session_sensitivity_state_maximum
    ON session_sensitivity_state(MaximumSensitivityCode);
