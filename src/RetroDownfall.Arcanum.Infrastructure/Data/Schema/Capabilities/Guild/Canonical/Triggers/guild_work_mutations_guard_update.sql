CREATE TRIGGER IF NOT EXISTS guild_work_mutations_guard_update BEFORE UPDATE ON guild_work_mutations
BEGIN SELECT RAISE(ABORT,'immutable work mutation'); END;
