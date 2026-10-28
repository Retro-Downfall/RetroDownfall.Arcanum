CREATE INDEX IF NOT EXISTS idx_long_rest_receipt_inputs_memory_receipt ON long_rest_receipt_inputs (lower(replace(MemoryId, '-', '')), ReceiptId, MemoryId);
