CREATE TRIGGER IF NOT EXISTS guild_workspace_authority_state_guard_update
BEFORE UPDATE ON guild_workspace_authority_state
BEGIN
    SELECT RAISE(ABORT, 'The installation-native operator principal is immutable.');
END;
