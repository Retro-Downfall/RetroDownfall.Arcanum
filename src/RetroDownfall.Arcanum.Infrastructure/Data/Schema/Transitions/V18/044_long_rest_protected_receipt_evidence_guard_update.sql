CREATE TRIGGER IF NOT EXISTS long_rest_protected_receipt_evidence_guard_update
BEFORE UPDATE ON long_rest_protected_receipt_evidence
BEGIN
    SELECT RAISE(ABORT, 'Long Rest accepted evidence is immutable.');
END;
