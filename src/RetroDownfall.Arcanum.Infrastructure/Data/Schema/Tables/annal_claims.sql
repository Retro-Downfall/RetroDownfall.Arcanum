-- A claim is the identity a durable assertion keeps across every correction. It binds to the exact row
-- that carries its content, and that binding lives here rather than on each version. A Lexicon
-- correction rewrites one row in place, so every revision of that claim names the same
-- lexicon_entries.Id; a per-version binding with a unique index over it would refuse the second
-- revision, and without the index two claims could quietly own one row.
CREATE TABLE IF NOT EXISTS annal_claims (
    ClaimId TEXT NOT NULL PRIMARY KEY,
    SubjectStoreCode INTEGER NOT NULL CHECK (SubjectStoreCode IN (1, 2)),
    SubjectId TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);

-- One durable row has at most one claim. This is also what makes the upgrade sweep idempotent: a batch
-- selects rows for which no claim exists, so the corpus shrinks by exactly the work that committed and
-- a re-run after a lost commit selects the same rows again rather than claiming them twice.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_claims_subject
ON annal_claims(SubjectStoreCode, SubjectId);

-- The candidate key annal_heads carries a composite foreign key to. It proves a head's store is the
-- store its own claim belongs to, which no single-column reference to ClaimId could.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_claims_store_candidate
ON annal_claims(ClaimId, SubjectStoreCode);
