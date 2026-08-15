-- External-content index: the tokens live here, the stored text lives in covenant_search_documents,
-- and content_rowid binds each FTS row to that table's SearchRowId. FTS5 stores no copy of the
-- columns, so it reads them back through the binding and the projection triggers must keep the two
-- in step by hand.
--
-- EntryId, LaneCode, and VersionId are UNINDEXED: a match must be returnable with the identity of the
-- head it came from, without those identifiers themselves being searchable terms that could match a
-- query for ordinary words.
CREATE VIRTUAL TABLE IF NOT EXISTS covenant_fts USING fts5(
    NormalizedKey,
    AuthoredContent,
    CompiledContent,
    EntryId UNINDEXED,
    LaneCode UNINDEXED,
    VersionId UNINDEXED,
    content='covenant_search_documents',
    content_rowid='SearchRowId',
    tokenize='unicode61 remove_diacritics 2 tokenchars ''._-''',
    prefix='2 3 4 8'
);
