-- Deleting the evidence while its parent still exists would strip a prepared child of the only
-- record of what was observed, leaving a replay free to invent a different observation for the same
-- intent. The foreign key's ON DELETE CASCADE is deliberately still permitted: it fires only after
-- the parent row is gone, and the parent's own delete guard already refuses a nonterminal intent.
CREATE TRIGGER IF NOT EXISTS campaign_path_full_reset_cleanup_evidence_guard_delete
BEFORE DELETE ON campaign_path_full_reset_cleanup_evidence
BEGIN
    SELECT RAISE(ABORT, 'Full reset cleanup evidence cannot be deleted while its intent exists.')
    WHERE EXISTS (SELECT 1 FROM campaign_path_marker_intents WHERE IntentId = OLD.IntentId);
END;
