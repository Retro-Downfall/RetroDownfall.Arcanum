CREATE TRIGGER IF NOT EXISTS Entries_campaign_contribution_insert
AFTER INSERT ON "Entries"
BEGIN
    UPDATE campaign_contribution_state SET SourceGeneration = SourceGeneration + 1
    WHERE SessionId = NEW.SessionId;
END;
