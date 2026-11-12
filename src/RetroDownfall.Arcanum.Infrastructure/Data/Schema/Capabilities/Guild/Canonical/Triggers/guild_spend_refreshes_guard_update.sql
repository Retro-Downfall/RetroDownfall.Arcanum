CREATE TRIGGER IF NOT EXISTS guild_spend_refreshes_guard_update
BEFORE UPDATE ON guild_spend_refreshes
WHEN NEW.RefreshId IS NOT OLD.RefreshId OR NEW.EffectId IS NOT OLD.EffectId OR NEW.OperationId IS NOT OLD.OperationId
    OR NEW.RequestDigest IS NOT OLD.RequestDigest OR (OLD.AcceptedReceiptId IS NOT NULL AND NEW.AcceptedReceiptId IS NOT OLD.AcceptedReceiptId)
    OR (NEW.AcceptedReceiptId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM guild_spend_receipts r
        WHERE r.ReceiptId=NEW.AcceptedReceiptId AND r.EffectId=NEW.EffectId))
BEGIN
    SELECT RAISE(ABORT,'Guild spend refresh identity is immutable.');
END;
