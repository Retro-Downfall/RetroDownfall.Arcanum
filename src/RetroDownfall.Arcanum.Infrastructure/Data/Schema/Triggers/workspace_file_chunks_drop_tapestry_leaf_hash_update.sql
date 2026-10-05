-- A stored hash describes the text it was computed from, so changing the text or the id drops it. The
-- WHEN clause leaves an update that rewrites the same value alone.
CREATE TRIGGER IF NOT EXISTS workspace_file_chunks_drop_tapestry_leaf_hash_update
AFTER UPDATE OF ChunkId, Content ON workspace_file_chunks
WHEN NEW.ChunkId IS NOT OLD.ChunkId OR NEW.Content IS NOT OLD.Content
BEGIN
    DELETE FROM tapestry_leaf_hashes
    WHERE SourceKind = 'WorkspaceFileChunk' AND SourceId IN (OLD.ChunkId, NEW.ChunkId);
END;
