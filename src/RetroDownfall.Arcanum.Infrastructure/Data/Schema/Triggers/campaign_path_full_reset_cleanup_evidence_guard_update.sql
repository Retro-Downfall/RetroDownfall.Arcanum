-- The whole value of this row is that it was written once, before either marker was touched, from
-- an observation nobody can repeat. There is no legal update: reconciliation advances the parent's
-- phase and compares this row, never rewrites it.
CREATE TRIGGER IF NOT EXISTS campaign_path_full_reset_cleanup_evidence_guard_update
BEFORE UPDATE ON campaign_path_full_reset_cleanup_evidence
BEGIN
    SELECT RAISE(ABORT, 'Full reset cleanup evidence is immutable once written.');
END;
