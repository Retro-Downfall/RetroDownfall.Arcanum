ALTER TABLE tapestry_generations ADD COLUMN SourceRevision INTEGER NOT NULL DEFAULT 0 CHECK (SourceRevision >= 0);
