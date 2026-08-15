-- A mutation receipt is the answer already returned for a request idempotency digest. A replay of
-- the same request is answered from this row, so rewriting it would let the second attempt receive a
-- different outcome than the first. A later mutation writes its own receipt instead.
CREATE TRIGGER IF NOT EXISTS covenant_mutation_receipts_guard_update
BEFORE UPDATE ON covenant_mutation_receipts
BEGIN
    SELECT RAISE(ABORT, 'covenant_mutation_receipts is append-only; existing rows cannot be updated.');
END;
