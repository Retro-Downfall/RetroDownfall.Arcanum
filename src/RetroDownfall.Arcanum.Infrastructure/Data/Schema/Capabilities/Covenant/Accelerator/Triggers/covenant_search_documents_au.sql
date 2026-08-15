-- An update is a subtraction of the old row followed by an insertion of the new one, in that order.
-- The 'delete' half must carry the complete OLD row, indexed and unindexed alike, or the superseded
-- tokens survive and a retired or rewritten head keeps matching its previous text.
CREATE TRIGGER IF NOT EXISTS covenant_search_documents_au
AFTER UPDATE ON covenant_search_documents
BEGIN
    INSERT INTO covenant_fts(covenant_fts, rowid, NormalizedKey, AuthoredContent, CompiledContent, EntryId, LaneCode, VersionId)
    VALUES ('delete', old.SearchRowId, old.NormalizedKey, old.AuthoredContent, old.CompiledContent, old.EntryId, old.LaneCode, old.VersionId);

    INSERT INTO covenant_fts(rowid, NormalizedKey, AuthoredContent, CompiledContent, EntryId, LaneCode, VersionId)
    VALUES (new.SearchRowId, new.NormalizedKey, new.AuthoredContent, new.CompiledContent, new.EntryId, new.LaneCode, new.VersionId);
END;
