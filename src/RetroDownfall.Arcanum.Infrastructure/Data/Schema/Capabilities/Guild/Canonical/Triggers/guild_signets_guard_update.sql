CREATE TRIGGER IF NOT EXISTS guild_signets_guard_update
BEFORE UPDATE ON guild_signets
WHEN NEW.SignetId IS NOT OLD.SignetId
  OR NEW.AgentId IS NOT OLD.AgentId
  OR NEW.RunId IS NOT OLD.RunId
  OR NEW.CommissionId IS NOT OLD.CommissionId
  OR NEW.RootCommissionId IS NOT OLD.RootCommissionId
  OR NEW.IssuerSignetId IS NOT OLD.IssuerSignetId
  OR NEW.GrantJson IS NOT OLD.GrantJson
  OR NEW.IssuedAtUtc IS NOT OLD.IssuedAtUtc
  OR NEW.OriginPrincipalId IS NOT OLD.OriginPrincipalId
  OR NEW.SessionId IS NOT OLD.SessionId
  OR NEW.CampaignId IS NOT OLD.CampaignId
  OR NEW.WorkspaceId IS NOT OLD.WorkspaceId
  OR NEW.WorkspaceIdentityPolicyVersion IS NOT OLD.WorkspaceIdentityPolicyVersion
  OR NEW.WorkspaceIdentityRevision IS NOT OLD.WorkspaceIdentityRevision
  OR NEW.WorkspaceRootIdentity IS NOT OLD.WorkspaceRootIdentity
  OR NEW.PeerId IS NOT OLD.PeerId
  OR NEW.PeerGrantRevision IS NOT OLD.PeerGrantRevision
  OR NEW.InstallationGeneration IS NOT OLD.InstallationGeneration
  OR NEW.AuthorityEpoch IS NOT OLD.AuthorityEpoch
  OR NEW.Revision<>OLD.Revision+1
  OR NEW.CredentialGeneration<OLD.CredentialGeneration
  OR NEW.CredentialGeneration>OLD.CredentialGeneration+1
  OR ((NEW.TokenDigest IS NOT OLD.TokenDigest)<>(NEW.CredentialGeneration=OLD.CredentialGeneration+1))
  OR NEW.RevocationGeneration<OLD.RevocationGeneration
  OR NEW.RevocationGeneration>OLD.RevocationGeneration+1
  OR NEW.Revoked<OLD.Revoked
  OR NEW.RestoredInert<OLD.RestoredInert
  OR NEW.Retired<OLD.Retired
  OR NEW.CancellationRequested<OLD.CancellationRequested
BEGIN
    SELECT RAISE(ABORT, 'Guild Signet identity and denial evidence are immutable.');
END;
