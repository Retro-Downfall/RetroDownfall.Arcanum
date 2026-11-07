CREATE TRIGGER IF NOT EXISTS guild_portal_inputs_guard_update
BEFORE UPDATE ON guild_portal_inputs
BEGIN
    SELECT RAISE(ABORT, 'Committed Guild Portal evidence is immutable.');
END;
