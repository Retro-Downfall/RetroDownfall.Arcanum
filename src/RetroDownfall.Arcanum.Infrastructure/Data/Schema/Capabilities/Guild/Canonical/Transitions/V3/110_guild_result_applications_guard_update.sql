CREATE TRIGGER IF NOT EXISTS guild_result_applications_guard_update
BEFORE UPDATE ON guild_result_applications
BEGIN
    SELECT RAISE(ABORT, 'Guild committed receipts and inputs are immutable.');
END;
