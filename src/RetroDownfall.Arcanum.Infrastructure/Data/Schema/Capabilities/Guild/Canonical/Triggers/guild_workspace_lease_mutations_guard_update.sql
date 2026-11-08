CREATE TRIGGER IF NOT EXISTS guild_workspace_lease_mutations_guard_update
BEFORE UPDATE ON guild_workspace_lease_mutations
BEGIN
    SELECT RAISE(ABORT, 'Guild workspace mutation receipts are immutable.');
END;
