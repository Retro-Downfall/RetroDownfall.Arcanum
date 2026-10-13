CREATE TRIGGER IF NOT EXISTS long_rest_dependency_insert
AFTER INSERT ON annal_dependencies
BEGIN
    UPDATE long_rest_sources SET MutationEpoch = MutationEpoch + 1 WHERE MemoryId IN (
        SELECT c.SubjectId FROM annal_claims c JOIN annal_versions v ON v.ClaimId = c.ClaimId
        WHERE c.SubjectStoreCode = 1 AND v.VersionId IN (NEW.DependentVersionId, NEW.DependencyVersionId));
END;
