-- A path operation receipt is the answer already returned for one owner operation and request
-- digest. A retry is answered from this row, so rewriting it would let the second attempt receive a
-- different result than the first, or make an old operation appear to have produced a path revision
-- it never created. A later operation writes its own receipt instead.
CREATE TRIGGER IF NOT EXISTS campaign_path_operation_receipts_guard_update
BEFORE UPDATE ON campaign_path_operation_receipts
BEGIN
    SELECT RAISE(ABORT, 'campaign_path_operation_receipts is append-only; existing rows cannot be updated.');
END;
