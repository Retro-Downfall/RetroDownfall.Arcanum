CREATE TRIGGER IF NOT EXISTS guild_quest_budget_guard_update
BEFORE UPDATE ON guild_quest_budget
BEGIN
    SELECT RAISE(ABORT, 'Guild Quest budget bindings are immutable.');
END;
