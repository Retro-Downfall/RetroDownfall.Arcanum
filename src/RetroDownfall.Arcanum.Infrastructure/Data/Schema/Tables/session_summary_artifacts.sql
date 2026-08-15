-- Every value that has ever occupied the mutable legacy Sessions.Summary gets one immutable
-- artifact row here, so a sensitivity label has a stable identity and revision to bind to. Without
-- it a label would describe a column that can be overwritten, and a stale label could authorize a
-- newer summary. The artifact carries its own content digest and watermark, so a replacement is
-- proven to match the exact bytes it labels.
CREATE TABLE IF NOT EXISTS session_summary_artifacts (
    ArtifactId TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    ContentDigest BLOB NOT NULL CHECK (length(ContentDigest) = 32),
    SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (0, 1)),
    SensitivityDigest BLOB NOT NULL CHECK (length(SensitivityDigest) = 32),
    SummarizedThroughUtc TEXT NULL,
    CreatedAtUtc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_session_summary_artifacts_session_revision
    ON session_summary_artifacts(SessionId, Revision);

-- The candidate key session_summary_state carries a composite foreign key to. It proves the current
-- pointer names an artifact that belongs to the same Session and carries the same revision, which a
-- single-column reference to ArtifactId could not.
CREATE UNIQUE INDEX IF NOT EXISTS ux_session_summary_artifacts_current_candidate
    ON session_summary_artifacts(ArtifactId, SessionId, Revision);
