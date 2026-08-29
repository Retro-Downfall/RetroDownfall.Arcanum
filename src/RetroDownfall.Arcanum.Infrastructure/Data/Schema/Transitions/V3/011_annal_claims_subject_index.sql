-- One durable row has at most one claim. This is also what makes the upgrade sweep idempotent: a batch
-- selects rows for which no claim exists, so the corpus shrinks by exactly the work that committed and
-- a re-run after a lost commit selects the same rows again rather than claiming them twice.
CREATE UNIQUE INDEX IF NOT EXISTS ux_annal_claims_subject
ON annal_claims(SubjectStoreCode, SubjectId);
