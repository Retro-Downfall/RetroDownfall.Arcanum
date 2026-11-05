CREATE TRIGGER IF NOT EXISTS guild_roster_refreshes_guard_update
BEFORE UPDATE ON guild_roster_refreshes
BEGIN
    SELECT RAISE(ABORT, 'Guild refresh receipts are immutable.');
END;
