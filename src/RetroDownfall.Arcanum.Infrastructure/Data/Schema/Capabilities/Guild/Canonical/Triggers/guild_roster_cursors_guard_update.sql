CREATE TRIGGER IF NOT EXISTS guild_roster_cursors_guard_update
BEFORE UPDATE ON guild_roster_cursors
BEGIN
    SELECT RAISE(ABORT, 'Guild continuation records are immutable.');
END;
