CREATE TRIGGER IF NOT EXISTS guild_remote_executions_guard_update
BEFORE UPDATE ON guild_remote_executions
BEGIN
    SELECT RAISE(ABORT,'Immutable Guild remote evidence');
END;
