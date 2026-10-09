CREATE TRIGGER IF NOT EXISTS long_rest_receipt_inputs_guard_update
BEFORE UPDATE ON long_rest_receipt_inputs
BEGIN
    SELECT RAISE(ABORT, 'long_rest_receipt_inputs is append-only; transformations are immutable.');
END;
