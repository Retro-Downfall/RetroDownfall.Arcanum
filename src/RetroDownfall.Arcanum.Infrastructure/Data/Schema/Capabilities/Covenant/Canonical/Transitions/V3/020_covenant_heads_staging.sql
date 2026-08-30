CREATE TEMP TABLE covenant_heads_v3_staging AS
SELECT
    EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode,
    ScopeCode, CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc
FROM covenant_heads;
