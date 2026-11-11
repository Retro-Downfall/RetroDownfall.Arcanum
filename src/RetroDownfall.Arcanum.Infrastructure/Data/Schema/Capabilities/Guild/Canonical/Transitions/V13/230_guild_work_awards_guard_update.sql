CREATE TRIGGER IF NOT EXISTS guild_work_awards_guard_update BEFORE UPDATE ON guild_work_awards
BEGIN SELECT RAISE(ABORT,'immutable work award'); END;
