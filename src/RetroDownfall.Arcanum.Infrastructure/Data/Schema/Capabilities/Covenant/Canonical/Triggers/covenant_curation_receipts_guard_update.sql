-- A curation receipt is the answer already returned for one request identity. A replay of the same
-- request is answered from this row, so rewriting it would let the second attempt receive a different
-- outcome than the first. A later change writes its own receipt instead.
CREATE TRIGGER IF NOT EXISTS covenant_curation_receipts_guard_update
BEFORE UPDATE ON covenant_curation_receipts
BEGIN
    SELECT RAISE(ABORT, 'covenant_curation_receipts is append-only; existing rows cannot be updated.');
END;
