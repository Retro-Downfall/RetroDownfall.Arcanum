-- BEFORE sees replacement conflicts even when recursive delete triggers are off.
-- SQLite also invokes it for a conflicting INSERT OR IGNORE: conservative invalidation is intentional.
CREATE TRIGGER IF NOT EXISTS workspace_file_chunks_tapestry_revision_insert
BEFORE INSERT ON workspace_file_chunks
BEGIN
    INSERT INTO tapestry_scope_revisions(ScopeKind,ScopeId,CorpusRevision,InvalidationRevision)
    SELECT DISTINCT 'Workspace', x.WorkspacePath, 1, 1 FROM workspace_file_chunks x
    WHERE (x.ChunkId = NEW.ChunkId) AND (x.Content IS NOT NULL AND trim(x.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> '') AND x.WorkspacePath IS NOT NEW.WorkspacePath
    ON CONFLICT(ScopeKind,ScopeId) DO UPDATE SET CorpusRevision=CorpusRevision+1, InvalidationRevision=InvalidationRevision+excluded.InvalidationRevision;
    INSERT INTO tapestry_scope_revisions(ScopeKind,ScopeId,CorpusRevision,InvalidationRevision)
    SELECT 'Workspace', NEW.WorkspacePath, 1, EXISTS(SELECT 1 FROM workspace_file_chunks x WHERE x.ChunkId = NEW.ChunkId)
    WHERE (NEW.Content IS NOT NULL AND trim(NEW.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> '') OR EXISTS(SELECT 1 FROM workspace_file_chunks x WHERE (x.ChunkId = NEW.ChunkId) AND x.WorkspacePath=NEW.WorkspacePath AND (x.Content IS NOT NULL AND trim(x.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> ''))
    ON CONFLICT(ScopeKind,ScopeId) DO UPDATE SET CorpusRevision=CorpusRevision+1, InvalidationRevision=InvalidationRevision+excluded.InvalidationRevision;
END;
