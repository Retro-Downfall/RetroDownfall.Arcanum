CREATE TRIGGER IF NOT EXISTS guild_local_executions_guard_update
BEFORE UPDATE ON guild_local_executions
BEGIN
    SELECT RAISE(ABORT, 'Guild local execution bindings are immutable.');
END;
