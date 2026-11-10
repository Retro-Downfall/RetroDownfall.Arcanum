CREATE TRIGGER IF NOT EXISTS guild_remote_questions_guard_update
BEFORE UPDATE ON guild_remote_questions
BEGIN
    SELECT RAISE(ABORT,'Immutable Guild remote evidence');
END;
