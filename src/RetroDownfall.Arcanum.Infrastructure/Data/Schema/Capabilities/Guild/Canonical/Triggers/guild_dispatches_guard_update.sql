CREATE TRIGGER IF NOT EXISTS guild_dispatches_guard_update
BEFORE UPDATE ON guild_dispatches
WHEN NEW.DispatchId IS NOT OLD.DispatchId
  OR NEW.QuestId IS NOT OLD.QuestId
  OR NEW.InvocationDigest IS NOT OLD.InvocationDigest
  OR NEW.IsContinuation IS NOT OLD.IsContinuation
  OR NEW.AccountingAssociationId IS NOT OLD.AccountingAssociationId
  OR NEW.CreatedAtUtc IS NOT OLD.CreatedAtUtc
  OR NEW.Revision<>OLD.Revision+1
  OR NOT (NEW.EvidenceCode=OLD.EvidenceCode OR (OLD.EvidenceCode=1 AND NEW.EvidenceCode IN(2,4)) OR (OLD.EvidenceCode=2 AND NEW.EvidenceCode IN(3,4)))
  OR (OLD.LocalChildId IS NOT NULL AND NEW.LocalChildId IS NOT OLD.LocalChildId)
  OR (OLD.RemoteTaskId IS NOT NULL AND NEW.RemoteTaskId IS NOT OLD.RemoteTaskId)
  OR (OLD.RemoteContextId IS NOT NULL AND NEW.RemoteContextId IS NOT OLD.RemoteContextId)
BEGIN
    SELECT RAISE(ABORT, 'Guild dispatch identity and physical evidence cannot be replaced or regressed.');
END;
