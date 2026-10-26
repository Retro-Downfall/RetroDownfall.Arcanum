CREATE TRIGGER IF NOT EXISTS session_attachment_chunks_tapestry_revision_delete
AFTER DELETE ON session_attachment_chunks
WHEN OLD.Content IS NOT NULL AND trim(OLD.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> '' AND OLD.RetrievalScope IS NOT NULL AND lower(trim(substr(OLD.MimeType,1,instr(OLD.MimeType || ';',';')-1),' ' || char(9) || char(10) || char(13))) NOT IN ('application/vnd.arcanum.comprehension-text', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'application/vnd.ms-word.document.macroenabled.12', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', 'application/vnd.ms-excel.sheet.macroenabled.12', 'application/vnd.openxmlformats-officedocument.presentationml.presentation', 'application/vnd.ms-powerpoint.presentation.macroenabled.12', 'application/vnd.oasis.opendocument.text', 'application/vnd.oasis.opendocument.spreadsheet', 'application/vnd.oasis.opendocument.presentation', 'application/vnd.ms-visio.drawing', 'application/vnd.ms-visio.drawing.macroenabled.12', 'application/pdf')
BEGIN
    INSERT INTO tapestry_scope_revisions(ScopeKind,ScopeId,CorpusRevision,InvalidationRevision)
    SELECT 'SessionAttachment', OLD.SessionId, 1, 1 WHERE 1
    ON CONFLICT(ScopeKind,ScopeId) DO UPDATE SET CorpusRevision=CorpusRevision+1, InvalidationRevision=InvalidationRevision+excluded.InvalidationRevision;
END;
