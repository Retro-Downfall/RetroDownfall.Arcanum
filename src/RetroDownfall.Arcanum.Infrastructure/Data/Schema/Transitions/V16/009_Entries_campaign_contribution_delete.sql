CREATE TRIGGER IF NOT EXISTS Entries_campaign_contribution_delete
AFTER DELETE ON "Entries"
BEGIN
    UPDATE campaign_contribution_state
    SET SourceGeneration = SourceGeneration + 1,
        CurrentArtifactId = CASE WHEN OLD.Sequence <= SummarizedThroughSequence AND OLD.Sequence > COALESCE(
        (SELECT InheritedThroughSequence FROM campaign_fork_frontiers WHERE SessionId = OLD.SessionId), 0) THEN NULL ELSE CurrentArtifactId END,
        RefoldRequired = CASE WHEN OLD.Sequence <= SummarizedThroughSequence AND OLD.Sequence > COALESCE(
        (SELECT InheritedThroughSequence FROM campaign_fork_frontiers WHERE SessionId = OLD.SessionId), 0) THEN 1 ELSE RefoldRequired END
    WHERE SessionId = OLD.SessionId;
END;
