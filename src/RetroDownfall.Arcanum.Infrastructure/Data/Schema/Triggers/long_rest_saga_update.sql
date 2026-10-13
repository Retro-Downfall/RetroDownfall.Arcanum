CREATE TRIGGER IF NOT EXISTS long_rest_saga_update
AFTER UPDATE OF Id, SessionId, Content, ScopeKindCode, CampaignId, RetiredAtUtc, PinnedAtUtc ON saga_memories
WHEN NEW.Id IS NOT OLD.Id OR NEW.SessionId IS NOT OLD.SessionId OR NEW.Content IS NOT OLD.Content OR NEW.ScopeKindCode IS NOT OLD.ScopeKindCode OR NEW.CampaignId IS NOT OLD.CampaignId
    OR NEW.RetiredAtUtc IS NOT OLD.RetiredAtUtc OR NEW.PinnedAtUtc IS NOT OLD.PinnedAtUtc
BEGIN
    DELETE FROM long_rest_sources WHERE MemoryId = OLD.Id AND NEW.Id <> OLD.Id;
    INSERT INTO long_rest_sources (MemoryId, MutationEpoch) SELECT NEW.Id, 1 WHERE NEW.Id <> OLD.Id;
    UPDATE long_rest_sources SET MutationEpoch = MutationEpoch + 1 WHERE MemoryId = OLD.Id;
END;
