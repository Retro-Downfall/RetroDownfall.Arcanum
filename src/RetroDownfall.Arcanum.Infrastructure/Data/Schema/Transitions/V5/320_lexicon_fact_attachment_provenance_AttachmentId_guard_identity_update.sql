-- lexicon_fact_attachment_provenance.AttachmentId is written by the Lexicon service and joins to
-- SessionAttachments."Id" by exact equality with NO foreign key at all. A divergent spelling makes an
-- attachment-derived fact report its source unavailable, permanently.
--
-- This table's EntryId names lexicon_entries.Id, which is one of the two columns that legitimately hold
-- the dash-free ToString("N") form, and its SessionId is not of this family either. Both are guarded by
-- nothing here, which is the whole reason this trigger is named for one column.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_update for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS lexicon_fact_attachment_provenance_AttachmentId_guard_identity_update
BEFORE UPDATE OF AttachmentId ON lexicon_fact_attachment_provenance
WHEN NEW.AttachmentId IS NOT NULL
    AND (NEW.AttachmentId <> upper(NEW.AttachmentId)
        OR length(NEW.AttachmentId) <> 36
        OR substr(NEW.AttachmentId, 9, 1) <> '-'
        OR substr(NEW.AttachmentId, 14, 1) <> '-'
        OR substr(NEW.AttachmentId, 19, 1) <> '-'
        OR substr(NEW.AttachmentId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'lexicon_fact_attachment_provenance.AttachmentId must be stored as an uppercase dashed 36-character identity.');
END;
