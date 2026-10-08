-- The row a source row's insert makes new has no stored hash yet. This also covers an INSERT OR REPLACE
-- over an existing source row, which removes the old row without firing the delete trigger.
CREATE TRIGGER IF NOT EXISTS session_attachment_chunks_drop_tapestry_leaf_hash_insert
AFTER INSERT ON session_attachment_chunks
BEGIN
    DELETE FROM tapestry_leaf_hashes
    WHERE SourceKind = 'SessionAttachmentChunk' AND SourceId = NEW.ChunkId;
END;
