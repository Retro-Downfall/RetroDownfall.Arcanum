CREATE TRIGGER IF NOT EXISTS guild_result_inputs_guard_update
BEFORE UPDATE ON guild_result_inputs
BEGIN
    SELECT RAISE(ABORT, 'Guild committed receipts and inputs are immutable.');
END;
