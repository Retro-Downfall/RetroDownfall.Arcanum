CREATE TABLE IF NOT EXISTS covenant_heads (
    EntryId TEXT NOT NULL,
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    CurrentVersionId TEXT NOT NULL,
    CurrentLaneRevision INTEGER NOT NULL CHECK (CurrentLaneRevision > 0),
    CurrentOperationCode INTEGER NOT NULL CHECK (CurrentOperationCode IN (1, 2)),
    ScopeCode INTEGER NOT NULL CHECK (ScopeCode IN (1, 2)),
    CampaignId TEXT NULL,
    NormalizedKey TEXT NOT NULL CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
    CompiledByteCost INTEGER NOT NULL CHECK (CompiledByteCost >= 0),
    OriginCode INTEGER NOT NULL CHECK (OriginCode IN (1, 2, 3)),
    SearchRowId INTEGER NOT NULL CHECK (SearchRowId > 0),
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (EntryId, LaneCode),
    -- The composite reference is the point: a plain FK to VersionId would let a head adopt a version
    -- belonging to another entry or lane, or one whose revision disagrees with the head's own.
    FOREIGN KEY (CurrentVersionId, EntryId, LaneCode, CurrentLaneRevision, CurrentOperationCode)
        REFERENCES covenant_versions(VersionId, EntryId, LaneCode, LaneRevision, OperationCode),
    FOREIGN KEY (EntryId) REFERENCES covenant_entries(EntryId),
    CHECK ((ScopeCode = 1 AND CampaignId IS NULL) OR (ScopeCode = 2 AND CampaignId IS NOT NULL)),
    -- No Global Proposed head. Agent-proposed content is Campaign-scoped by construction, so a
    -- proposal cannot silently apply to every Campaign on the installation.
    CHECK (NOT (ScopeCode = 1 AND LaneCode = 2))
);

-- The projection row ID is the accelerator's stable identity. It is allocated once per entry and
-- lane and never reused within a dataset generation, so an FTS row can never be claimed by a
-- different head after a delete and recreate.
CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_heads_search_row
    ON covenant_heads(SearchRowId);

CREATE UNIQUE INDEX IF NOT EXISTS ux_covenant_heads_current_version
    ON covenant_heads(CurrentVersionId);

CREATE INDEX IF NOT EXISTS idx_covenant_heads_global_active
    ON covenant_heads(NormalizedKey, LaneCode, CurrentOperationCode) WHERE CampaignId IS NULL;

CREATE INDEX IF NOT EXISTS idx_covenant_heads_campaign_active
    ON covenant_heads(CampaignId, NormalizedKey, LaneCode, CurrentOperationCode) WHERE CampaignId IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_covenant_heads_campaign_cleanup
    ON covenant_heads(CampaignId);
