CREATE TRIGGER IF NOT EXISTS guild_inbox_guard_update
BEFORE UPDATE ON guild_inbox
WHEN NEW.Id IS NOT OLD.Id
  OR NEW.EnvelopeId IS NOT OLD.EnvelopeId
  OR NEW.RecipientKey IS NOT OLD.RecipientKey
  OR NEW.RecipientQuestId IS NOT OLD.RecipientQuestId
  OR NEW.StreamId IS NOT OLD.StreamId
  OR NEW.SenderSequence IS NOT OLD.SenderSequence
  OR NEW.CreatedAtUtc IS NOT OLD.CreatedAtUtc
  OR NEW.Revision<>OLD.Revision+1
  OR OLD.DeliveryStateCode<>1
  OR NOT (NEW.DeliveryStateCode=1 OR NEW.DeliveryStateCode IN(2,3))
BEGIN
    SELECT RAISE(ABORT, 'Guild Portal inbox identity and committed acknowledgement cannot be replaced or regressed.');
END;
