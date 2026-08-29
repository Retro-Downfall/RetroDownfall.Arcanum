-- ScopeCampaignId is laid out the way SQLite lays out an added column rather than the way the columns
-- above it are indented; see the same note in saga_memories.sql for why reindenting it reports
-- DefinitionDrift on every installation evolved from version 1.
--
-- The empty string, not NULL, is the global scope. SQLite treats NULLs as distinct in a UNIQUE index,
-- so a nullable scope column would let one global name be inserted any number of times and quietly
-- undo the uniqueness this table has always had. NOT NULL DEFAULT '' also means the upgrade needs no
-- sweep: every existing row is global the moment the column exists, which is exactly the behaviour
-- preservation this change promises.
CREATE TABLE IF NOT EXISTS lexicon_entries (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    NameNormalized TEXT NOT NULL,
    Type TEXT NOT NULL,
    FactsJson TEXT NOT NULL,
    FactsText TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
, ScopeCampaignId TEXT NOT NULL DEFAULT '');

-- Scope first: every lookup knows which scope it is asking about before it knows the name, and the
-- two-tier match reads one scope and then the other.
CREATE UNIQUE INDEX IF NOT EXISTS IX_lexicon_entries_Scope_NameNormalized
ON lexicon_entries(ScopeCampaignId, NameNormalized);
