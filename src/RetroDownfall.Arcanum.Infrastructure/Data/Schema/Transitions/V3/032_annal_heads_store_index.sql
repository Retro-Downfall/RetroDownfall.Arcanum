-- A store-scoped erasure reads this to find the heads it must release before the versions may go.
CREATE INDEX IF NOT EXISTS idx_annal_heads_store
ON annal_heads(SubjectStoreCode);
