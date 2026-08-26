-- The derived transaction-time end resolves a version's successor through this column, and an erasure
-- walks the same edge.
CREATE INDEX IF NOT EXISTS idx_annal_versions_predecessor
ON annal_versions(PredecessorVersionId);
