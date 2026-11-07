CREATE TRIGGER IF NOT EXISTS guild_portal_cursors_guard_update
BEFORE UPDATE ON guild_portal_cursors
BEGIN
    SELECT RAISE(ABORT, 'Committed Guild Portal evidence is immutable.');
END;
