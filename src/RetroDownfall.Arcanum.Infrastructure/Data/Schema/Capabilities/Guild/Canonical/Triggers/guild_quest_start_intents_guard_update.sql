CREATE TRIGGER IF NOT EXISTS guild_quest_start_intents_guard_update
BEFORE UPDATE ON guild_quest_start_intents
BEGIN
    SELECT RAISE(ABORT, 'Guild committed receipts and inputs are immutable.');
END;
