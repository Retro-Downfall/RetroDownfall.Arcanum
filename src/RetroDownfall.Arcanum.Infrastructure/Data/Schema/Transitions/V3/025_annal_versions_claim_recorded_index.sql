-- Reading one claim's history in order, which is the shape every consumer of this table wants.
CREATE INDEX IF NOT EXISTS idx_annal_versions_claim_recorded
ON annal_versions(ClaimId, RecordedAtUtc);
