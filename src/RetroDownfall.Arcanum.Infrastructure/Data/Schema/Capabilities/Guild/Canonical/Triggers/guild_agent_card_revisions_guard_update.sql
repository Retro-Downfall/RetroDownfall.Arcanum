CREATE TRIGGER IF NOT EXISTS guild_agent_card_revisions_guard_update
BEFORE UPDATE ON guild_agent_card_revisions
BEGIN
    SELECT RAISE(ABORT, 'Guild content and evidence revisions are immutable.');
END;
