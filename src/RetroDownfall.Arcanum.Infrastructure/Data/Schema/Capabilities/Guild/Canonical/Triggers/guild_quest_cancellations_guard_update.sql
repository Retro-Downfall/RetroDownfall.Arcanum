CREATE TRIGGER IF NOT EXISTS guild_quest_cancellations_guard_update
BEFORE UPDATE ON guild_quest_cancellations
BEGIN
    SELECT RAISE(ABORT, 'Guild cancellation receipts are immutable.');
END;
