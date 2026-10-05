-- A stored hash describes the text it was computed from, so changing the text or the id drops it. The
-- WHEN clause leaves an update that rewrites the same value alone.
CREATE TRIGGER IF NOT EXISTS session_attachment_chunks_drop_tapestry_leaf_hash_update
AFTER UPDATE OF ChunkId, Content ON session_attachment_chunks
WHEN NEW.ChunkId IS NOT OLD.ChunkId OR NEW.Content IS NOT OLD.Content
BEGIN
    DELETE FROM tapestry_leaf_hashes
    WHERE SourceKind = 'SessionAttachmentChunk' AND SourceId IN (OLD.ChunkId, NEW.ChunkId);
END;
