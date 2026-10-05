-- A deleted source row leaves no text for a stored hash to describe.
CREATE TRIGGER IF NOT EXISTS workspace_file_chunks_drop_tapestry_leaf_hash_delete
AFTER DELETE ON workspace_file_chunks
BEGIN
    DELETE FROM tapestry_leaf_hashes
    WHERE SourceKind = 'WorkspaceFileChunk' AND SourceId = OLD.ChunkId;
END;
