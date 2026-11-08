CREATE TRIGGER IF NOT EXISTS guild_workspace_leases_guard_insert
BEFORE INSERT ON guild_workspace_leases
WHEN EXISTS(SELECT 1 FROM guild_workspace_lease_mutations mutation WHERE mutation.PrincipalId=NEW.PrincipalId
    AND mutation.QuestId IS NEW.QuestId AND mutation.IdempotencyKey=NEW.IdempotencyKey)
BEGIN
    SELECT RAISE(ABORT, 'The Guild workspace mutation key already names an accepted intent.');
END;
