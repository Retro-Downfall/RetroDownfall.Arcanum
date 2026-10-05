-- A deleted source row leaves no text for a stored hash to describe.
CREATE TRIGGER IF NOT EXISTS session_attachment_chunks_drop_tapestry_leaf_hash_delete
AFTER DELETE ON session_attachment_chunks
BEGIN
    DELETE FROM tapestry_leaf_hashes
    WHERE SourceKind = 'SessionAttachmentChunk' AND SourceId = OLD.ChunkId;
END;
