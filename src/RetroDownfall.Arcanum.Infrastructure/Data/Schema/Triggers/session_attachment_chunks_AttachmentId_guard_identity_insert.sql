-- session_attachment_chunks.AttachmentId is written by the attachment index repository under a real
-- foreign key to SessionAttachments(Id), so a spelling that diverged from its parent would be refused by
-- the schema rather than silently returning nothing.
--
-- This table's SessionId and RetrievalScope are deliberately NOT of this family and are guarded by
-- nothing here. The tapestry reads SELECT DISTINCT SessionId from this table as its live scope-id set and
-- those values become tapestry_nodes.ScopeId, so moving them would orphan every attachment-scoped
-- generation and rebuild the tree at provider cost. That is why this guard names one column.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS session_attachment_chunks_AttachmentId_guard_identity_insert
BEFORE INSERT ON session_attachment_chunks
WHEN NEW.AttachmentId IS NOT NULL
    AND (NEW.AttachmentId <> upper(NEW.AttachmentId)
        OR length(NEW.AttachmentId) <> 36
        OR substr(NEW.AttachmentId, 9, 1) <> '-'
        OR substr(NEW.AttachmentId, 14, 1) <> '-'
        OR substr(NEW.AttachmentId, 19, 1) <> '-'
        OR substr(NEW.AttachmentId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'session_attachment_chunks.AttachmentId must be stored as an uppercase dashed 36-character identity.');
END;
