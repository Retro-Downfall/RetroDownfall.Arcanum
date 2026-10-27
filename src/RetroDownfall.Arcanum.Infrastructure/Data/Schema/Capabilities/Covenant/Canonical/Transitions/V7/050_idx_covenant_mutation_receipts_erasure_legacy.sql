CREATE INDEX IF NOT EXISTS idx_covenant_mutation_receipts_erasure_legacy
    ON covenant_mutation_receipts(ScopeCode, lower(replace(CampaignId, '-', '')), CommittedAtUtc) WHERE EntryId IS NULL AND OutcomeCode = 2;
