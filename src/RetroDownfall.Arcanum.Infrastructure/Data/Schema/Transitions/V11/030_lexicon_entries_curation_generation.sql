ALTER TABLE lexicon_entries ADD COLUMN CurationGeneration INTEGER NOT NULL DEFAULT 1 CHECK (CurationGeneration > 0);
