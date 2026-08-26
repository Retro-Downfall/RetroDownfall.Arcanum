CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_versions_claim_revision
ON annal_versions(ClaimId, Revision);
