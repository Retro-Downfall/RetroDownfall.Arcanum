CREATE TRIGGER IF NOT EXISTS guild_remote_callbacks_guard_update
BEFORE UPDATE ON guild_remote_callbacks
WHEN OLD.CallbackConfigId IS NOT NEW.CallbackConfigId OR OLD.QuestId IS NOT NEW.QuestId
    OR OLD.DispatchId IS NOT NEW.DispatchId OR OLD.CallbackTokenHash IS NOT NEW.CallbackTokenHash
    OR (OLD.CallbackReceivedAtUtc IS NOT NULL AND OLD.CallbackReceivedAtUtc IS NOT NEW.CallbackReceivedAtUtc)
BEGIN
    SELECT RAISE(ABORT,'Immutable Guild remote callback correlation');
END;
