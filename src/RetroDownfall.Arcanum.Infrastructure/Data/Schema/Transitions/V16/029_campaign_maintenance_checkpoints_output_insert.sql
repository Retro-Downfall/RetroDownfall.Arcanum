CREATE TRIGGER IF NOT EXISTS campaign_maintenance_checkpoints_output_insert
BEFORE INSERT ON campaign_maintenance_checkpoints
WHEN NEW.CheckpointStateCode = 2 AND NOT (
    (NEW.StepCode = 5 AND EXISTS (SELECT 1 FROM campaign_rollup_artifacts
        WHERE ArtifactId = NEW.OutputArtifactId AND CampaignId = NEW.CampaignId AND Revision = NEW.OutputRevision
            AND ContentDigest = NEW.OutputContentDigest AND SensitivityDigest = NEW.OutputSensitivityDigest))
    OR (NEW.StepCode = 6 AND EXISTS (SELECT 1 FROM campaign_contribution_artifacts
        WHERE ArtifactId = NEW.OutputArtifactId AND CampaignId = NEW.CampaignId AND SessionId = NEW.SourceSessionId AND Revision = NEW.OutputRevision
            AND ContentDigest = NEW.OutputContentDigest AND SensitivityDigest = NEW.OutputSensitivityDigest)))
BEGIN
    SELECT RAISE(ABORT, 'Campaign maintenance output must bind an exact immutable publication.');
END;
