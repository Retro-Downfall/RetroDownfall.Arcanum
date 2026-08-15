-- An external-content index is not populated by the insert into its content table. Every new
-- projection row has to be handed to covenant_fts explicitly, under the same rowid, or the head
-- exists in the projection and is unfindable by search.
CREATE TRIGGER IF NOT EXISTS covenant_search_documents_ai
AFTER INSERT ON covenant_search_documents
BEGIN
    INSERT INTO covenant_fts(rowid, NormalizedKey, AuthoredContent, CompiledContent, EntryId, LaneCode, VersionId)
    VALUES (new.SearchRowId, new.NormalizedKey, new.AuthoredContent, new.CompiledContent, new.EntryId, new.LaneCode, new.VersionId);
END;
