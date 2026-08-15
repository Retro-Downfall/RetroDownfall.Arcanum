-- The current-summary pointer, added as its own projection because issue #74 cannot ALTER TABLE the
-- EF-owned Sessions row. The composite foreign key is the point of the table: it can only ever name
-- an artifact that belongs to this Session and carries this exact revision, so a replacement that
-- swapped in another Session's artifact, or reused a revision, fails at the boundary instead of
-- leaving the projection pointing at evidence for different bytes.
CREATE TABLE IF NOT EXISTS session_summary_state (
    SessionId TEXT NOT NULL PRIMARY KEY REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    CurrentArtifactId TEXT NOT NULL,
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    UpdatedAtUtc TEXT NOT NULL,
    -- Cascading from the artifact as well as from the Session keeps whole-Session retention from
    -- deleting the artifact first and stranding a pointer to a row that no longer exists.
    FOREIGN KEY (CurrentArtifactId, SessionId, Revision)
        REFERENCES session_summary_artifacts(ArtifactId, SessionId, Revision) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_session_summary_state_artifact
    ON session_summary_state(CurrentArtifactId);
