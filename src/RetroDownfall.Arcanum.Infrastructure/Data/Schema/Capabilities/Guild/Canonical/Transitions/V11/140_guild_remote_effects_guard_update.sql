CREATE TRIGGER IF NOT EXISTS guild_remote_effects_guard_update
BEFORE UPDATE ON guild_remote_effects
WHEN OLD.EffectId IS NOT NEW.EffectId OR OLD.QuestId IS NOT NEW.QuestId OR OLD.DispatchId IS NOT NEW.DispatchId
    OR (OLD.StopReasonCode IS NOT NULL AND OLD.StopReasonCode IS NOT NEW.StopReasonCode)
    OR (OLD.SettledAtUtc IS NOT NULL AND OLD.SettledAtUtc IS NOT NEW.SettledAtUtc)
BEGIN
    SELECT RAISE(ABORT,'Immutable Guild remote effect correlation');
END;
