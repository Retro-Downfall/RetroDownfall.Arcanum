CREATE TRIGGER IF NOT EXISTS guild_memory_attribution_guard_update
BEFORE UPDATE ON guild_memory_attribution
BEGIN
    SELECT RAISE(ABORT,'Guild memory attribution is immutable.');
END;
