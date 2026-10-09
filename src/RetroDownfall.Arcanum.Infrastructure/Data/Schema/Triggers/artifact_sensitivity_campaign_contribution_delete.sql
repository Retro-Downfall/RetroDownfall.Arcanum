CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_campaign_contribution_delete
AFTER DELETE ON artifact_sensitivity
WHEN OLD.ArtifactKindCode = 1
BEGIN
    UPDATE campaign_contribution_state
    SET SourceGeneration = SourceGeneration + 1,
        CurrentArtifactId = CASE WHEN EXISTS (SELECT 1 FROM "Entries" e WHERE e.Id = upper(substr(replace(OLD.ArtifactId, '-', ''),1,8)||'-'||substr(replace(OLD.ArtifactId, '-', ''),9,4)||'-'||substr(replace(OLD.ArtifactId, '-', ''),13,4)||'-'||substr(replace(OLD.ArtifactId, '-', ''),17,4)||'-'||substr(replace(OLD.ArtifactId, '-', ''),21,12))
        AND e.SessionId = campaign_contribution_state.SessionId AND e.Sequence <= SummarizedThroughSequence
        AND e.Sequence > COALESCE((SELECT InheritedThroughSequence FROM campaign_fork_frontiers f WHERE f.SessionId = e.SessionId), 0)) THEN NULL ELSE CurrentArtifactId END,
        RefoldRequired = CASE WHEN EXISTS (SELECT 1 FROM "Entries" e WHERE e.Id = upper(substr(replace(OLD.ArtifactId, '-', ''),1,8)||'-'||substr(replace(OLD.ArtifactId, '-', ''),9,4)||'-'||substr(replace(OLD.ArtifactId, '-', ''),13,4)||'-'||substr(replace(OLD.ArtifactId, '-', ''),17,4)||'-'||substr(replace(OLD.ArtifactId, '-', ''),21,12))
        AND e.SessionId = campaign_contribution_state.SessionId AND e.Sequence <= SummarizedThroughSequence
        AND e.Sequence > COALESCE((SELECT InheritedThroughSequence FROM campaign_fork_frontiers f WHERE f.SessionId = e.SessionId), 0)) THEN 1 ELSE RefoldRequired END
    WHERE SessionId IN (SELECT SessionId FROM "Entries" WHERE Id = upper(substr(replace(OLD.ArtifactId, '-', ''),1,8)||'-'||substr(replace(OLD.ArtifactId, '-', ''),9,4)||'-'||substr(replace(OLD.ArtifactId, '-', ''),13,4)||'-'||substr(replace(OLD.ArtifactId, '-', ''),17,4)||'-'||substr(replace(OLD.ArtifactId, '-', ''),21,12)));
END;
