CREATE TRIGGER IF NOT EXISTS campaign_rollup_sources_guard_insert
BEFORE INSERT ON campaign_rollup_sources
WHEN NOT EXISTS (SELECT 1 FROM campaign_contribution_artifacts
    WHERE ArtifactId = NEW.ContributionArtifactId AND SessionId = NEW.SessionId
        AND CampaignId = NEW.CampaignId AND Revision = NEW.ContributionRevision
        AND ContentDigest = NEW.ContentDigest AND SensitivityDigest = NEW.SensitivityDigest
        AND SummarizedThroughSequence = NEW.SummarizedThroughSequence)
BEGIN
    SELECT RAISE(ABORT, 'A Campaign source manifest must bind exact contribution evidence.');
END;
