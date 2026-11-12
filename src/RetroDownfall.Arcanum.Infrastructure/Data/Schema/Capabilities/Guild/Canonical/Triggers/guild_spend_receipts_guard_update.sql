CREATE TRIGGER IF NOT EXISTS guild_spend_receipts_guard_update
BEFORE UPDATE ON guild_spend_receipts
BEGIN
    SELECT RAISE(ABORT,'Guild spend receipts are immutable.');
END;
