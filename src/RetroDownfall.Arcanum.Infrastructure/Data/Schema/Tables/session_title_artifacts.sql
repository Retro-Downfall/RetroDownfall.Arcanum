-- The same immutable artifact contract session_summary_artifacts gives the mutable legacy
-- Sessions.Summary, applied to the mutable legacy Sessions.Title. A model-generated title
-- propagates taint, so a title needs an identity and revision a sensitivity label can bind to; a
-- clean operator-authored replacement can then remove the prior tainted label in the same
-- transaction that overwrites the title. A title has no summarization watermark.
CREATE TABLE IF NOT EXISTS session_title_artifacts (
    ArtifactId TEXT NOT NULL PRIMARY KEY,
    SessionId TEXT NOT NULL REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    ContentDigest BLOB NOT NULL CHECK (length(ContentDigest) = 32),
    SensitivityCode INTEGER NOT NULL CHECK (SensitivityCode IN (0, 1)),
    SensitivityDigest BLOB NOT NULL CHECK (length(SensitivityDigest) = 32),
    CreatedAtUtc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_session_title_artifacts_session_revision
    ON session_title_artifacts(SessionId, Revision);

-- The candidate key session_title_state carries a composite foreign key to, so the current pointer
-- cannot name another Session's artifact or a mismatched revision.
CREATE UNIQUE INDEX IF NOT EXISTS ux_session_title_artifacts_current_candidate
    ON session_title_artifacts(ArtifactId, SessionId, Revision);
