CREATE TRIGGER IF NOT EXISTS guild_portal_outbox_guard_update
BEFORE UPDATE ON guild_portal_outbox
WHEN NEW.SourceKindCode IS NOT OLD.SourceKindCode
  OR NEW.ReceiptId IS NOT OLD.ReceiptId
  OR NEW.QuestId IS NOT OLD.QuestId
  OR NEW.CreatedAtUtc IS NOT OLD.CreatedAtUtc
  OR OLD.DeliveryStateCode<>1
  OR NEW.DeliveryStateCode NOT IN(1,2,3)
  OR OLD.EnvelopeId IS NOT NULL AND NEW.EnvelopeId IS NOT OLD.EnvelopeId
BEGIN
    SELECT RAISE(ABORT, 'Guild Portal receipt outbox identity and committed envelope are immutable.');
END;
