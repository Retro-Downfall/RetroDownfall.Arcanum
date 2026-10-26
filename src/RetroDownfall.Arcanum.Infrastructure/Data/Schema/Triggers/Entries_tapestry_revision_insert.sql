-- Replacement detection is explicit; recursive delete triggers need not be enabled.
-- Conflicting INSERT OR IGNORE conservatively invalidates; no source writer uses it for retries.
CREATE TRIGGER IF NOT EXISTS Entries_tapestry_revision_insert
BEFORE INSERT ON Entries
BEGIN
    INSERT INTO tapestry_scope_revisions(ScopeKind,ScopeId,CorpusRevision,InvalidationRevision)
    SELECT DISTINCT 'Session', x.SessionId, 1, 1 FROM Entries x
    WHERE (x.Id=NEW.Id OR (x.SessionId=NEW.SessionId AND x.Sequence=NEW.Sequence)) AND (x.Content IS NOT NULL AND trim(x.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> '') AND x.SessionId IS NOT NEW.SessionId
    ON CONFLICT(ScopeKind,ScopeId) DO UPDATE SET CorpusRevision=CorpusRevision+1, InvalidationRevision=InvalidationRevision+excluded.InvalidationRevision;
    INSERT INTO tapestry_scope_revisions(ScopeKind,ScopeId,CorpusRevision,InvalidationRevision)
    SELECT 'Session', NEW.SessionId, 1, EXISTS(SELECT 1 FROM Entries x WHERE x.Id=NEW.Id OR (x.SessionId=NEW.SessionId AND x.Sequence=NEW.Sequence))
    WHERE (NEW.Content IS NOT NULL AND trim(NEW.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> '') OR EXISTS(SELECT 1 FROM Entries x WHERE (x.Id=NEW.Id OR (x.SessionId=NEW.SessionId AND x.Sequence=NEW.Sequence)) AND x.SessionId=NEW.SessionId AND (x.Content IS NOT NULL AND trim(x.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> ''))
    ON CONFLICT(ScopeKind,ScopeId) DO UPDATE SET CorpusRevision=CorpusRevision+1, InvalidationRevision=InvalidationRevision+excluded.InvalidationRevision;
    DELETE FROM tapestry_leaf_hashes WHERE SourceKind='Entry' AND SourceId IN (SELECT x.Id FROM Entries x WHERE x.Id=NEW.Id OR (x.SessionId=NEW.SessionId AND x.Sequence=NEW.Sequence));
END;
