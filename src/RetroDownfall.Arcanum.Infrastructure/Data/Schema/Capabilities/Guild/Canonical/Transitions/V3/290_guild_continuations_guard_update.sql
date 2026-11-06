CREATE TRIGGER IF NOT EXISTS guild_continuations_guard_update
BEFORE UPDATE ON guild_continuations
BEGIN
    SELECT RAISE(ABORT, 'Guild accepted continuation receipts are immutable.');
END;
