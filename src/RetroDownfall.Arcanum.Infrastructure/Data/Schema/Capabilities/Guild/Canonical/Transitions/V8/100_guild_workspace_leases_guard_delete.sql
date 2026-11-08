CREATE TRIGGER IF NOT EXISTS guild_workspace_leases_guard_delete
BEFORE DELETE ON guild_workspace_leases
WHEN OLD.StateCode IS NOT 3 OR OLD.InFlightMutationCount IS NOT 0
BEGIN
    SELECT RAISE(ABORT, 'Protected Guild workspace mutation ownership cannot be erased.');
END;
