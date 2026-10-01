-- Triggers and the version-11 projection rebuild index active entries only. The external content
-- table also holds retired entries, so FTS5's unfiltered 'rebuild' command must never be used here.
--
-- From Core version 13 the index runs with secure-delete = 1. Transitions/V13/060 sets it, and
-- CoreGrimoireSchemaDataInitializer converges it and reads it back on every install, so a deleted or
-- replaced row leaves no token in lexicon_fts_data or lexicon_fts_idx. Transitions/V13/070 ran
-- 'optimize' once, to merge away the residue that deletes before version 13 left in older segments.
-- A Lexicon erase that finds secure-delete off turns it back on and runs 'optimize' again before it
-- deletes anything, inside its own transaction; nothing else runs it.
CREATE VIRTUAL TABLE IF NOT EXISTS lexicon_fts USING fts5(
    Name,
    Type,
    FactsText,
    content='lexicon_entries',
    content_rowid='rowid'
);
