CREATE TRIGGER IF NOT EXISTS guild_event_streams_guard_update
BEFORE UPDATE ON guild_event_streams
WHEN NEW.RootQuestId IS NOT OLD.RootQuestId
  OR NEW.LastSequence<OLD.LastSequence
  OR NEW.RetainedAfterSequence<OLD.RetainedAfterSequence
BEGIN
    SELECT RAISE(ABORT, 'Guild Portal event sequence and retention watermarks are monotonic.');
END;
