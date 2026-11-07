CREATE TRIGGER IF NOT EXISTS guild_portal_receipts_guard_update
BEFORE UPDATE ON guild_portal_receipts
BEGIN
    SELECT RAISE(ABORT, 'Committed Guild Portal evidence is immutable.');
END;
