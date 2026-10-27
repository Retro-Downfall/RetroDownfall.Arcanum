CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_erasure_entry
    ON covenant_mutation_receipts(lower(replace(EntryId, '-', '')));
