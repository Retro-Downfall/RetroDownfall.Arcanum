-- SessionAttachments."EntryId" is the Entry an attachment was bound to, NULL until the turn that binds
-- it commits. Compared against Entries."Id" with no foreign key, so it carries the same hazard its
-- sibling does and is repaired the same way.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS SessionAttachments_EntryId_guard_identity_insert
BEFORE INSERT ON "SessionAttachments"
WHEN NEW."EntryId" IS NOT NULL
    AND (NEW."EntryId" <> upper(NEW."EntryId")
        OR length(NEW."EntryId") <> 36
        OR substr(NEW."EntryId", 9, 1) <> '-'
        OR substr(NEW."EntryId", 14, 1) <> '-'
        OR substr(NEW."EntryId", 19, 1) <> '-'
        OR substr(NEW."EntryId", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'SessionAttachments.EntryId must be stored as an uppercase dashed 36-character identity.');
END;
