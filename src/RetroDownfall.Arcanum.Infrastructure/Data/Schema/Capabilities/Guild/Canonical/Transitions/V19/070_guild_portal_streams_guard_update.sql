CREATE TRIGGER IF NOT EXISTS guild_portal_streams_guard_update
BEFORE UPDATE ON guild_portal_streams
WHEN NEW.StreamId IS NOT OLD.StreamId
  OR NEW.QuestId IS NOT OLD.QuestId
  OR NEW.ProducerKindCode IS NOT OLD.ProducerKindCode
  OR NEW.ProducerKey IS NOT OLD.ProducerKey
  OR NEW.LastSequence<>OLD.LastSequence+1
BEGIN
    SELECT RAISE(ABORT, 'Guild Portal stream identity is immutable and its sequence advances once.');
END;
