-- attachment_memory_consultations.AttachmentId is written by the attachment-memory provenance store and
-- joins to SessionAttachments."Id" by exact equality with NO foreign key at all. Nothing but the sweep's
-- own declaration pairs it with its parent, and a divergent spelling makes an attachment-derived
-- consultation report its source unavailable, permanently, on every installation.
--
-- This table's SourceEntryId and SessionId are not of this family and are guarded by nothing here, which
-- is why this trigger is named for the column rather than for the table.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS attachment_memory_consultations_AttachmentId_guard_identity_insert
BEFORE INSERT ON attachment_memory_consultations
WHEN NEW.AttachmentId IS NOT NULL
    AND (NEW.AttachmentId <> upper(NEW.AttachmentId)
        OR length(NEW.AttachmentId) <> 36
        OR substr(NEW.AttachmentId, 9, 1) <> '-'
        OR substr(NEW.AttachmentId, 14, 1) <> '-'
        OR substr(NEW.AttachmentId, 19, 1) <> '-'
        OR substr(NEW.AttachmentId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'attachment_memory_consultations.AttachmentId must be stored as an uppercase dashed 36-character identity.');
END;
