CREATE TRIGGER IF NOT EXISTS Entries_campaign_contribution_update
AFTER UPDATE ON "Entries"
WHEN NEW.Content IS NOT OLD.Content OR NEW.Role IS NOT OLD.Role OR NEW.ModelUsed IS NOT OLD.ModelUsed
    OR NEW.ToolCallId IS NOT OLD.ToolCallId OR NEW.ToolName IS NOT OLD.ToolName OR NEW.ToolArguments IS NOT OLD.ToolArguments
    OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.Sequence IS NOT OLD.Sequence OR NEW.SessionId IS NOT OLD.SessionId
BEGIN
    UPDATE campaign_contribution_state
    SET SourceGeneration = SourceGeneration + 1,
        CurrentArtifactId = CASE WHEN OLD.Sequence <= SummarizedThroughSequence AND OLD.Sequence > COALESCE(
        (SELECT InheritedThroughSequence FROM campaign_fork_frontiers WHERE SessionId = OLD.SessionId), 0) THEN NULL ELSE CurrentArtifactId END,
        RefoldRequired = CASE WHEN OLD.Sequence <= SummarizedThroughSequence AND OLD.Sequence > COALESCE(
        (SELECT InheritedThroughSequence FROM campaign_fork_frontiers WHERE SessionId = OLD.SessionId), 0) THEN 1 ELSE RefoldRequired END
    WHERE SessionId = OLD.SessionId;
    UPDATE campaign_contribution_state SET SourceGeneration = SourceGeneration + 1
    WHERE SessionId = NEW.SessionId AND NEW.SessionId IS NOT OLD.SessionId;
END;
