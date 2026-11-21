CREATE TRIGGER IF NOT EXISTS TR_guild_remote_portal_deliveries_Transition
BEFORE UPDATE ON guild_remote_portal_deliveries
WHEN NEW.InboxId IS NOT OLD.InboxId OR NEW.RecipientQuestId IS NOT OLD.RecipientQuestId
  OR NEW.EnvelopeId IS NOT OLD.EnvelopeId OR NEW.TaskId IS NOT OLD.TaskId OR NEW.ContextId IS NOT OLD.ContextId
  OR NEW.TargetDigest IS NOT OLD.TargetDigest
  OR NEW.TransportStateCode<OLD.TransportStateCode OR NEW.TransportStateCode>OLD.TransportStateCode+1
  OR (OLD.WireIntentDigest IS NOT NULL AND NEW.WireIntentDigest IS NOT OLD.WireIntentDigest)
  OR (OLD.PeerReceiptId IS NOT NULL AND NEW.PeerReceiptId IS NOT OLD.PeerReceiptId)
  OR (OLD.PeerRevision IS NOT NULL AND NEW.PeerRevision<OLD.PeerRevision)
  OR (OLD.PeerDeliveryStateCode IN(2,3) AND (NEW.PeerDeliveryStateCode IS NOT OLD.PeerDeliveryStateCode
      OR NEW.PeerApplicationReceiptId IS NOT OLD.PeerApplicationReceiptId))
  OR (OLD.PeerRevision IS NOT NULL AND NEW.PeerRevision=OLD.PeerRevision
      AND (NEW.PeerDeliveryStateCode IS NOT OLD.PeerDeliveryStateCode OR NEW.PeerApplicationReceiptId IS NOT OLD.PeerApplicationReceiptId))
  OR NEW.ObservationCount<OLD.ObservationCount OR NEW.ObservationCount>OLD.ObservationCount+1
BEGIN
    SELECT RAISE(ABORT, 'Guild remote Portal evidence must advance monotonically under its original binding.');
END;
