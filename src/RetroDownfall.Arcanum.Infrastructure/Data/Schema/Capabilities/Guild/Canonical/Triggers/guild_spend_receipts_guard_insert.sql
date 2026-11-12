CREATE TRIGGER IF NOT EXISTS guild_spend_receipts_guard_insert
BEFORE INSERT ON guild_spend_receipts
WHEN NOT EXISTS(SELECT 1 FROM guild_budget_evidence e JOIN guild_budget_effects f ON f.EffectId=e.EffectId
    WHERE e.ReceiptId=NEW.EvidenceReceiptId AND e.EffectId=NEW.EffectId AND e.AppliedRevision=NEW.EvidenceRevision
      AND ((NEW.OriginCode=1 AND f.KindCode=1) OR (NEW.OriginCode IN(0,2,3) AND f.KindCode=0)))
BEGIN
    SELECT RAISE(ABORT,'Guild spend receipt requires its exact physical evidence.');
END;
