CREATE TABLE IF NOT EXISTS covenant_search_documents (
    -- Declared INTEGER PRIMARY KEY so it is the SQLite rowid itself, not a second column beside one.
    -- covenant_fts binds content_rowid to this value, so an FTS row and its projection row share one
    -- identity and cannot drift apart.
    SearchRowId INTEGER PRIMARY KEY CHECK (SearchRowId > 0),
    -- No foreign key to covenant_heads, covenant_entries, or any core owner table. This projection is
    -- rebuildable from canonical state, so it must never be able to block a canonical mutation or an
    -- owner deletion by holding a reference. Divergence is repaired by a rebuild, not by a constraint.
    EntryId TEXT NOT NULL,
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    VersionId TEXT NOT NULL,
    ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL,
    LifecycleCode INTEGER NOT NULL CHECK (LifecycleCode IN (1, 2)),
    NormalizedKey TEXT NOT NULL,
    AuthoredContent TEXT NULL,
    CompiledContent TEXT NULL,
    DatasetGeneration BLOB NOT NULL CHECK (length(DatasetGeneration) = 16),
    CanonicalSearchSequence INTEGER NOT NULL CHECK (CanonicalSearchSequence >= 0),
    CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL)),
    -- A current tombstone indexes its key and lifecycle only. Retaining content on a retired head
    -- would let search return text the operator asked to remove, and would leave those tokens in the
    -- index behind a row that reads as deleted.
    CHECK (
        (LifecycleCode = 1 AND AuthoredContent IS NOT NULL AND CompiledContent IS NOT NULL)
        OR (LifecycleCode = 2 AND AuthoredContent IS NULL AND CompiledContent IS NULL)
    )
);

-- One projection row per head. The accelerator is keyed by entry and lane exactly as covenant_heads
-- is, so a duplicate here would mean the same head matched twice in one search result.
CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_search_documents_entry_lane
    ON covenant_search_documents(EntryId, LaneCode);

CREATE INDEX IF NOT EXISTS idx_covenant_search_documents_campaign
    ON covenant_search_documents(CampaignId);
