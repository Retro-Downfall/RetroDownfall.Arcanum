CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_erasure_version
    ON covenant_mutation_receipts(lower(replace(ResultingVersionId, '-', '')));
