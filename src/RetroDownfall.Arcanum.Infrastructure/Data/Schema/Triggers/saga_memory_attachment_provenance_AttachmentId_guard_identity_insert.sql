-- saga_memory_attachment_provenance.AttachmentId is written by the Saga memory store and joins to
-- SessionAttachments."Id" by exact equality with NO foreign key at all. A divergent spelling makes an
-- attachment-derived memory report its source unavailable, permanently.
--
-- This table's SessionId is not of this family and is guarded by nothing here.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS saga_memory_attachment_provenance_AttachmentId_guard_identity_insert
BEFORE INSERT ON saga_memory_attachment_provenance
WHEN NEW.AttachmentId IS NOT NULL
    AND (NEW.AttachmentId <> upper(NEW.AttachmentId)
        OR length(NEW.AttachmentId) <> 36
        OR substr(NEW.AttachmentId, 9, 1) <> '-'
        OR substr(NEW.AttachmentId, 14, 1) <> '-'
        OR substr(NEW.AttachmentId, 19, 1) <> '-'
        OR substr(NEW.AttachmentId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'saga_memory_attachment_provenance.AttachmentId must be stored as an uppercase dashed 36-character identity.');
END;
