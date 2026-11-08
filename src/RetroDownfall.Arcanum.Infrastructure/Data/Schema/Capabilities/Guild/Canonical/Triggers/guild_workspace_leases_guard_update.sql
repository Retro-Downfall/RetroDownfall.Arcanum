CREATE TRIGGER IF NOT EXISTS guild_workspace_leases_guard_update
BEFORE UPDATE ON guild_workspace_leases
WHEN NEW.Id IS NOT OLD.Id
  OR NEW.OwnerKindCode IS NOT OLD.OwnerKindCode
  OR NEW.PrincipalId IS NOT OLD.PrincipalId
  OR NEW.QuestId IS NOT OLD.QuestId
  OR NEW.RootQuestId IS NOT OLD.RootQuestId
  OR NEW.SessionId IS NOT OLD.SessionId
  OR NEW.CampaignId IS NOT OLD.CampaignId
  OR NEW.WorkspaceId IS NOT OLD.WorkspaceId
  OR NEW.InstallationGeneration IS NOT OLD.InstallationGeneration
  OR NEW.WorkspaceRoot IS NOT OLD.WorkspaceRoot
  OR NEW.IdentityJson IS NOT OLD.IdentityJson
  OR NEW.IdentityDigest IS NOT OLD.IdentityDigest
  OR NEW.EffectKindCode IS NOT OLD.EffectKindCode
  OR NEW.IdempotencyKey IS NOT OLD.IdempotencyKey
  OR NEW.RequestDigest IS NOT OLD.RequestDigest
  OR NEW.Revision<>OLD.Revision+1
  OR NEW.Epoch<OLD.Epoch
  OR NEW.Epoch>OLD.Epoch+1
  OR (NEW.HostInstanceId IS NOT OLD.HostInstanceId AND NEW.Epoch<>OLD.Epoch+1)
  OR (OLD.StateCode=3 AND NEW.StateCode<>3)
BEGIN
    SELECT RAISE(ABORT, 'Guild workspace lease authority and identity are immutable.');
END;
