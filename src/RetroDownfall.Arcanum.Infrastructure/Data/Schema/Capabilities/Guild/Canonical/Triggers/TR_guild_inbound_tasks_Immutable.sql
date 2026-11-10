CREATE TRIGGER IF NOT EXISTS TR_guild_inbound_tasks_Immutable BEFORE UPDATE ON guild_inbound_tasks
BEGIN SELECT RAISE(ABORT,'inbound task identity is immutable'); END;
