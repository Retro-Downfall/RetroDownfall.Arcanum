CREATE TRIGGER IF NOT EXISTS long_rest_receipts_guard_update
BEFORE UPDATE ON long_rest_receipts
BEGIN
    SELECT RAISE(ABORT, 'long_rest_receipts is append-only; transformations are immutable.');
END;
