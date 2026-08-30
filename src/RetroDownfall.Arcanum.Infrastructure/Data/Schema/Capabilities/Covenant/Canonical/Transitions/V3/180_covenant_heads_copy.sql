INSERT INTO covenant_heads (
    EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode,
    ScopeCode, CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc
)
SELECT
    EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode,
    ScopeCode, CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc
FROM temp.covenant_heads_v3_staging;
