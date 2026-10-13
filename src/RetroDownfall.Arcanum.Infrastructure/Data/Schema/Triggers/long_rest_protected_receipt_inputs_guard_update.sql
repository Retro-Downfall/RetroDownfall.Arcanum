CREATE TRIGGER IF NOT EXISTS long_rest_protected_receipt_inputs_guard_update
BEFORE UPDATE ON long_rest_protected_receipt_inputs
BEGIN
    SELECT RAISE(ABORT, 'Long Rest accepted evidence is immutable.');
END;
