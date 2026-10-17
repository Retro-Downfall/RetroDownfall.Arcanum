CREATE TRIGGER IF NOT EXISTS long_rest_head_delete
AFTER DELETE ON annal_heads
BEGIN
    UPDATE long_rest_sources SET MutationEpoch = MutationEpoch + 1 WHERE MemoryId IN (
        SELECT SubjectId FROM annal_claims WHERE SubjectStoreCode = 1 AND ClaimId = OLD.ClaimId
        UNION SELECT c.SubjectId FROM annal_dependencies d JOIN annal_versions v ON v.VersionId = d.DependencyVersionId
            JOIN annal_claims c ON c.ClaimId = v.ClaimId AND c.SubjectStoreCode = 1
            WHERE d.DependentVersionId = OLD.CurrentVersionId);
END;
