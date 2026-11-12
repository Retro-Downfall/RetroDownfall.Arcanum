CREATE TRIGGER IF NOT EXISTS guild_operator_sequences_guard_update
BEFORE UPDATE ON guild_operator_sequences
BEGIN
    SELECT RAISE(ABORT,'Guild operator membership is immutable.');
END;
