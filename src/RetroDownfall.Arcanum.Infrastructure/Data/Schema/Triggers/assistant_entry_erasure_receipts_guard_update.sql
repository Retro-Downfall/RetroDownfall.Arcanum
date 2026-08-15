-- The receipt is a tombstone. Its guard digest, reason, operation, and timestamp are the record of
-- which purge removed which committed artifact, and rewriting any of them would let a later
-- operation claim credit for an erasure it did not perform, or point the receipt at a different
-- turn. A new erasure is a new receipt for a different assistant entry.
CREATE TRIGGER IF NOT EXISTS assistant_entry_erasure_receipts_guard_update
BEFORE UPDATE ON assistant_entry_erasure_receipts
BEGIN
    SELECT RAISE(ABORT, 'assistant_entry_erasure_receipts rows are immutable tombstones.');
END;
