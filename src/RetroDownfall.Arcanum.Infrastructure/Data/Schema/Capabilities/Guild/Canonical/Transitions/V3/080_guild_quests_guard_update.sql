CREATE TRIGGER IF NOT EXISTS guild_quests_guard_update
BEFORE UPDATE ON guild_quests
WHEN NEW.Id IS NOT OLD.Id
  OR NEW.OperationId IS NOT OLD.OperationId
  OR NEW.InstallationGeneration IS NOT OLD.InstallationGeneration
  OR NEW.IssuerIdentity IS NOT OLD.IssuerIdentity
  OR NEW.IssuerKindCode IS NOT OLD.IssuerKindCode
  OR NEW.IssuingStepId IS NOT OLD.IssuingStepId
  OR NEW.LogicalCallId IS NOT OLD.LogicalCallId
  OR NEW.MutationId IS NOT OLD.MutationId
  OR NEW.IntentDigest IS NOT OLD.IntentDigest
  OR NEW.ResolutionDigest IS NOT OLD.ResolutionDigest
  OR NEW.RootQuestId IS NOT OLD.RootQuestId
  OR NEW.ParentQuestId IS NOT OLD.ParentQuestId
  OR NEW.AgentId IS NOT OLD.AgentId
  OR NEW.CardId IS NOT OLD.CardId
  OR NEW.CardRevision IS NOT OLD.CardRevision
  OR NEW.RunId IS NOT OLD.RunId
  OR NEW.SignetId IS NOT OLD.SignetId
  OR (NEW.Name IS NOT OLD.Name AND NEW.Name IS NOT NULL)
  OR NEW.SessionId IS NOT OLD.SessionId
  OR NEW.CampaignId IS NOT OLD.CampaignId
  OR NEW.WorkspaceId IS NOT OLD.WorkspaceId
  OR NEW.GoalContentId IS NOT OLD.GoalContentId
  OR NEW.GoalRevision IS NOT OLD.GoalRevision
  OR NEW.GoalDigest IS NOT OLD.GoalDigest
  OR NEW.GoalMimeType IS NOT OLD.GoalMimeType
  OR NEW.GoalByteLength IS NOT OLD.GoalByteLength
  OR NEW.AllocationAccountId IS NOT OLD.AllocationAccountId
  OR NEW.CreatedAtUtc IS NOT OLD.CreatedAtUtc
  OR NEW.IssuedSequence IS NOT OLD.IssuedSequence
  OR NEW.Revision<>OLD.Revision+1
  OR NEW.CancellationRequested<OLD.CancellationRequested
  OR NEW.CancellationConverged<OLD.CancellationConverged
  OR NEW.Retired<OLD.Retired
BEGIN
    SELECT RAISE(ABORT, 'Guild Quest identity and denial evidence are immutable.');
END;
