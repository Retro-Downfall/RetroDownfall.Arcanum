-- The current-title pointer. Direct title update, fork, retention, and reset all go through this
-- projection rather than writing Sessions.Title alone, so no caller can leave a title whose
-- sensitivity label describes bytes that are no longer there. The composite foreign key ties the
-- pointer to one artifact of this Session at this exact revision.
CREATE TABLE IF NOT EXISTS session_title_state (
    SessionId TEXT NOT NULL PRIMARY KEY REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    CurrentArtifactId TEXT NOT NULL,
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    UpdatedAtUtc TEXT NOT NULL,
    -- Cascading from the artifact as well as from the Session keeps whole-Session retention from
    -- deleting the artifact first and stranding a pointer to a row that no longer exists.
    FOREIGN KEY (CurrentArtifactId, SessionId, Revision)
        REFERENCES session_title_artifacts(ArtifactId, SessionId, Revision) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_session_title_state_artifact
    ON session_title_state(CurrentArtifactId);
