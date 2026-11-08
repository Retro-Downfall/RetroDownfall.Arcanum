CREATE TRIGGER IF NOT EXISTS guild_workspace_lease_mutations_guard_insert
BEFORE INSERT ON guild_workspace_lease_mutations
WHEN NOT EXISTS(SELECT 1 FROM guild_workspace_leases lease WHERE lease.Id=NEW.LeaseId
    AND lease.PrincipalId=NEW.PrincipalId AND lease.QuestId IS NEW.QuestId)
  OR (NEW.ActionCode=3 AND NOT EXISTS(SELECT 1 FROM guild_workspace_leases lease WHERE lease.Id=NEW.LeaseId
    AND lease.PrincipalId=NEW.PrincipalId AND lease.QuestId IS NEW.QuestId
    AND lease.IdempotencyKey=NEW.IdempotencyKey AND lease.RequestDigest=NEW.RequestDigest
    AND lease.Epoch=1 AND lease.Revision=1 AND lease.StateCode=1 AND lease.InFlightMutationCount=0
    AND lease.HeartbeatAtUtc=NEW.AcceptedAtUtc))
  OR (NEW.ActionCode<>3 AND EXISTS(SELECT 1 FROM guild_workspace_leases lease WHERE lease.PrincipalId=NEW.PrincipalId
    AND lease.QuestId IS NEW.QuestId AND lease.IdempotencyKey=NEW.IdempotencyKey))
BEGIN
    SELECT RAISE(ABORT, 'The Guild workspace mutation key or acquisition acceptance does not match its lease.');
END;
