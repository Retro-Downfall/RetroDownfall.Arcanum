CREATE TRIGGER IF NOT EXISTS Entries_tapestry_revision_delete
AFTER DELETE ON Entries
WHEN OLD.Content IS NOT NULL AND trim(OLD.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> ''
BEGIN
    INSERT INTO tapestry_scope_revisions(ScopeKind,ScopeId,CorpusRevision,InvalidationRevision)
    SELECT 'Session', OLD.SessionId, 1, 1 WHERE 1
    ON CONFLICT(ScopeKind,ScopeId) DO UPDATE SET CorpusRevision=CorpusRevision+1, InvalidationRevision=InvalidationRevision+excluded.InvalidationRevision;
END;
