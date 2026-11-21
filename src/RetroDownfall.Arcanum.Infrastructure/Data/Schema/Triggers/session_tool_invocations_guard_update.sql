CREATE TRIGGER IF NOT EXISTS session_tool_invocations_guard_update
BEFORE UPDATE ON session_tool_invocations
WHEN NEW.ReceiptId IS NOT OLD.ReceiptId OR NEW.SessionId IS NOT OLD.SessionId
    OR NEW.AssistantEntryId IS NOT OLD.AssistantEntryId OR NEW.ClaimId IS NOT OLD.ClaimId
    OR NEW.RoundOrdinal IS NOT OLD.RoundOrdinal OR NEW.CallOrdinal IS NOT OLD.CallOrdinal
    OR NEW.ArgumentsDigest IS NOT OLD.ArgumentsDigest OR NEW.CreatedAtUtcTicks IS NOT OLD.CreatedAtUtcTicks
    OR NOT ((OLD.StateCode=1 AND NEW.StateCode=2)
         OR (OLD.StateCode=2 AND NEW.StateCode=1)
         OR (OLD.StateCode=2 AND NEW.StateCode=3 AND NEW.DispatchId IS OLD.DispatchId))
BEGIN
    SELECT RAISE(ABORT,'Session tool invocation evidence must advance under its recorded identity.');
END;
