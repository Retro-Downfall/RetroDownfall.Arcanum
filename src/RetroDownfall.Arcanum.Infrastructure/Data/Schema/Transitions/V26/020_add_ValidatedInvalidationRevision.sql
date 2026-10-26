ALTER TABLE tapestry_generations ADD COLUMN ValidatedInvalidationRevision INTEGER CHECK (ValidatedInvalidationRevision >= 0);
