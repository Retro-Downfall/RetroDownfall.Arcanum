CREATE TRIGGER IF NOT EXISTS TR_guild_inbound_portal_messages_Immutable
BEFORE UPDATE ON guild_inbound_portal_messages
BEGIN
    SELECT RAISE(ABORT, 'Committed Guild peer message bindings are immutable.');
END;
