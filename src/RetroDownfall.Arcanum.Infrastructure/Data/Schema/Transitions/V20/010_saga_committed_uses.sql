-- Exact exposure evidence is owned jointly by the native reply and the Saga source.
-- Finalization time and successful outcome come from the retained one-shot guard.
CREATE TABLE IF NOT EXISTS saga_committed_uses (
    AssistantEntryId TEXT NOT NULL REFERENCES assistant_entry_finalizations(AssistantEntryId) ON DELETE CASCADE,
    MemoryId TEXT NOT NULL REFERENCES saga_memories(Id) ON DELETE CASCADE CHECK (length(CAST(MemoryId AS BLOB)) BETWEEN 1 AND 1024),
    ClaimId TEXT NOT NULL REFERENCES annal_claims(ClaimId) ON DELETE CASCADE CHECK (length(CAST(ClaimId AS BLOB)) BETWEEN 1 AND 1024),
    VersionId TEXT NOT NULL REFERENCES annal_versions(VersionId) ON DELETE CASCADE CHECK (length(CAST(VersionId AS BLOB)) BETWEEN 1 AND 1024),
    HashFormat INTEGER NOT NULL CHECK (HashFormat = 1),
    ContentDigest BLOB NOT NULL CHECK (length(ContentDigest) = 32),
    PRIMARY KEY (AssistantEntryId, MemoryId, VersionId),
    FOREIGN KEY (AssistantEntryId) REFERENCES "Entries"(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_saga_committed_uses_version ON saga_committed_uses(MemoryId, VersionId);

CREATE INDEX IF NOT EXISTS ix_saga_committed_uses_claim ON saga_committed_uses(ClaimId);

CREATE INDEX IF NOT EXISTS ix_saga_committed_uses_source_version ON saga_committed_uses(VersionId);
