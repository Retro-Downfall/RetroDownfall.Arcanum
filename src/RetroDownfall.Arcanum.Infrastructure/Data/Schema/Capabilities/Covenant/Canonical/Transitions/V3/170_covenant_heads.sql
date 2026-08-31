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
