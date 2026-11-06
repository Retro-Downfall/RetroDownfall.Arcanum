CREATE TRIGGER IF NOT EXISTS guild_result_receipts_guard_update
BEFORE UPDATE ON guild_result_receipts
BEGIN
    SELECT RAISE(ABORT, 'Guild committed receipts and inputs are immutable.');
END;
