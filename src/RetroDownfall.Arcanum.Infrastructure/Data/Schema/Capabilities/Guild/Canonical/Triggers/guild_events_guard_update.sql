CREATE TRIGGER IF NOT EXISTS guild_events_guard_update
BEFORE UPDATE ON guild_events
BEGIN
    SELECT RAISE(ABORT, 'Committed Guild Portal evidence is immutable.');
END;
