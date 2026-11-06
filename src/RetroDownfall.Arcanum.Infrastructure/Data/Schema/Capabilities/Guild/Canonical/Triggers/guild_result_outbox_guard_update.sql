CREATE TRIGGER IF NOT EXISTS guild_result_outbox_guard_update
BEFORE UPDATE ON guild_result_outbox
WHEN NEW.QuestId IS NOT OLD.QuestId
  OR NEW.ResultId IS NOT OLD.ResultId
  OR NEW.RecipientQuestId IS NOT OLD.RecipientQuestId
  OR NEW.Digest IS NOT OLD.Digest
  OR NEW.CreatedAtUtc IS NOT OLD.CreatedAtUtc
  OR NEW.Revision<>OLD.Revision+1
  OR NOT (NEW.DeliveryStateCode=OLD.DeliveryStateCode OR (OLD.DeliveryStateCode=1 AND NEW.DeliveryStateCode IN(2,3)))
  OR (OLD.AcknowledgedAtUtc IS NOT NULL AND NEW.AcknowledgedAtUtc IS NOT OLD.AcknowledgedAtUtc)
BEGIN
    SELECT RAISE(ABORT, 'Guild result delivery identity and acknowledgements cannot be replaced or regressed.');
END;
