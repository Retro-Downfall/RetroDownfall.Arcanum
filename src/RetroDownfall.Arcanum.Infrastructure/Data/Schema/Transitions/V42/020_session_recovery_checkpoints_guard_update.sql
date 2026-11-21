CREATE TRIGGER IF NOT EXISTS session_recovery_checkpoints_guard_update
BEFORE UPDATE ON session_recovery_checkpoints
WHEN NEW.Id IS NOT OLD.Id OR NEW.SessionId IS NOT OLD.SessionId OR NEW.AssistantEntryId IS NOT OLD.AssistantEntryId
    OR NEW.ClaimId IS NOT OLD.ClaimId OR NEW.KindCode IS NOT OLD.KindCode OR NEW.Provider IS NOT OLD.Provider
    OR NEW.Model IS NOT OLD.Model OR NEW.ProviderRouteDigest IS NOT OLD.ProviderRouteDigest OR NEW.CampaignId IS NOT OLD.CampaignId
    OR NEW.Workspace IS NOT OLD.Workspace OR NEW.WorkspaceIdentity IS NOT OLD.WorkspaceIdentity
    OR NEW.QuestionEntryId IS NOT OLD.QuestionEntryId OR NEW.InvocationId IS NOT OLD.InvocationId
    OR NEW.LastVerifiedInvocationId IS NOT OLD.LastVerifiedInvocationId OR NEW.CreatedAtUtcTicks IS NOT OLD.CreatedAtUtcTicks
    OR OLD.ConsumingClaimId IS NOT NULL OR NEW.ConsumingClaimId IS NULL
BEGIN
    SELECT RAISE(ABORT,'A Session recovery checkpoint can be consumed once under its recorded identity.');
END;
