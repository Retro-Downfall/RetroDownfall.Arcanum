-- Dropped before its replacement is created, because the old index is what made one normalized name
-- mean one entity installation-wide, and that is the constraint this version is retiring.
DROP INDEX IF EXISTS IX_lexicon_entries_NameNormalized;
