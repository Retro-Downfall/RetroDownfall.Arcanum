-- The candidate key annal_heads carries a composite foreign key to. It proves a head's store is the
-- store its own claim belongs to, which no single-column reference to ClaimId could.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_claims_store_candidate
ON annal_claims(ClaimId, SubjectStoreCode);
