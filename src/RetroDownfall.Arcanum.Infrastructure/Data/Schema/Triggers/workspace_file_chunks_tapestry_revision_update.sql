CREATE TRIGGER IF NOT EXISTS workspace_file_chunks_tapestry_revision_update
AFTER UPDATE OF ChunkId, WorkspacePath, RelativePath, Content ON workspace_file_chunks
WHEN NEW.ChunkId IS NOT OLD.ChunkId OR NEW.WorkspacePath IS NOT OLD.WorkspacePath OR NEW.RelativePath IS NOT OLD.RelativePath OR NEW.Content IS NOT OLD.Content
BEGIN
    INSERT INTO tapestry_scope_revisions(ScopeKind,ScopeId,CorpusRevision,InvalidationRevision)
    SELECT 'Workspace', OLD.WorkspacePath, 1, 1
    WHERE (OLD.Content IS NOT NULL AND trim(OLD.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> '') AND (OLD.WorkspacePath IS NOT NEW.WorkspacePath OR NOT (NEW.Content IS NOT NULL AND trim(NEW.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> ''))
    ON CONFLICT(ScopeKind,ScopeId) DO UPDATE SET CorpusRevision=CorpusRevision+1, InvalidationRevision=InvalidationRevision+excluded.InvalidationRevision;
    INSERT INTO tapestry_scope_revisions(ScopeKind,ScopeId,CorpusRevision,InvalidationRevision)
    SELECT 'Workspace', NEW.WorkspacePath, 1, 1
    WHERE (NEW.Content IS NOT NULL AND trim(NEW.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> '')
    ON CONFLICT(ScopeKind,ScopeId) DO UPDATE SET CorpusRevision=CorpusRevision+1, InvalidationRevision=InvalidationRevision+excluded.InvalidationRevision;
END;
