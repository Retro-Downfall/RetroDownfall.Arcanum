-- The 'delete' command carries every old value, indexed and unindexed alike, because FTS5 subtracts
-- the tokens it is given rather than looking them up: the content row is already gone by the time the
-- trigger runs, so anything omitted here stays behind as a ghost token matching a row that no longer
-- exists. This is also why the projection uses this idiom and never a plain DELETE FROM covenant_fts
-- WHERE rowid, which corrupts an external-content index.
CREATE TRIGGER IF NOT EXISTS covenant_search_documents_ad
AFTER DELETE ON covenant_search_documents
BEGIN
    INSERT INTO covenant_fts(covenant_fts, rowid, NormalizedKey, AuthoredContent, CompiledContent, EntryId, LaneCode, VersionId)
    VALUES ('delete', old.SearchRowId, old.NormalizedKey, old.AuthoredContent, old.CompiledContent, old.EntryId, old.LaneCode, old.VersionId);
END;
