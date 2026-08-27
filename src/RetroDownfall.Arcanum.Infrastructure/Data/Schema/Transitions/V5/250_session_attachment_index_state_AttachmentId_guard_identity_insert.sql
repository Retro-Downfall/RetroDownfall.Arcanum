-- session_attachment_index_state.AttachmentId is written by the attachment index repository under a real
-- foreign key to SessionAttachments(Id), and is this table's TEXT PRIMARY KEY - which SQLite still allows
-- to be NULL, so the guard qualifies on NULL rather than relying on the declaration.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS session_attachment_index_state_AttachmentId_guard_identity_insert
BEFORE INSERT ON session_attachment_index_state
WHEN NEW.AttachmentId IS NOT NULL
    AND (NEW.AttachmentId <> upper(NEW.AttachmentId)
        OR length(NEW.AttachmentId) <> 36
        OR substr(NEW.AttachmentId, 9, 1) <> '-'
        OR substr(NEW.AttachmentId, 14, 1) <> '-'
        OR substr(NEW.AttachmentId, 19, 1) <> '-'
        OR substr(NEW.AttachmentId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'session_attachment_index_state.AttachmentId must be stored as an uppercase dashed 36-character identity.');
END;
