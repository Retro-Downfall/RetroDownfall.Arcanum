CREATE TRIGGER IF NOT EXISTS annal_review_events_head_insert AFTER INSERT ON annal_heads BEGIN
    INSERT INTO annal_review_events (VersionId, ClaimId, SubjectStoreCode, SubjectId, OperationCode, OriginCode, ScopeKindCode, CampaignId, SourceSessionId)
    SELECT version.VersionId, claim.ClaimId, claim.SubjectStoreCode, claim.SubjectId, version.OperationCode, version.OriginCode, version.ScopeKindCode, version.CampaignId, version.SourceSessionId
    FROM annal_versions AS version JOIN annal_claims AS claim ON claim.ClaimId = version.ClaimId WHERE version.VersionId = new.CurrentVersionId;
END;
