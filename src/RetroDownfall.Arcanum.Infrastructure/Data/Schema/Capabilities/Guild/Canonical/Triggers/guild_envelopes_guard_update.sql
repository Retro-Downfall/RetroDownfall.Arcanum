CREATE TRIGGER IF NOT EXISTS guild_envelopes_guard_update
BEFORE UPDATE ON guild_envelopes
BEGIN
    SELECT RAISE(ABORT, 'Committed Guild Portal evidence is immutable.');
END;
