CREATE TRIGGER IF NOT EXISTS guild_local_wait_checkpoints_guard_update
BEFORE UPDATE ON guild_local_wait_checkpoints
BEGIN
    SELECT RAISE(ABORT, 'Guild wait checkpoint bindings are immutable');
END;
