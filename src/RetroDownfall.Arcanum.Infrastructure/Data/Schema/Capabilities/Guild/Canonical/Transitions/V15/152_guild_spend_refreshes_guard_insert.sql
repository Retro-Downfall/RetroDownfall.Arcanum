CREATE TRIGGER IF NOT EXISTS guild_spend_refreshes_guard_insert
BEFORE INSERT ON guild_spend_refreshes
WHEN NEW.AcceptedReceiptId IS NOT NULL AND NOT EXISTS(
    SELECT 1 FROM guild_spend_receipts r WHERE r.ReceiptId=NEW.AcceptedReceiptId AND r.EffectId=NEW.EffectId)
BEGIN
    SELECT RAISE(ABORT,'Guild spend refresh receipt belongs to another effect.');
END;
