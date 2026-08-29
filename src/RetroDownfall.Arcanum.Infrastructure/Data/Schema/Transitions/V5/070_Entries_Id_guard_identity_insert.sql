-- Entries."Id" is written by the object-relational writer, and by the protected artifact transfer store
-- and the backup session importer, which each mint a fresh identity for every Entry they import rather
-- than carrying the source installation's.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS Entries_Id_guard_identity_insert
BEFORE INSERT ON "Entries"
WHEN NEW."Id" IS NOT NULL
    AND (NEW."Id" <> upper(NEW."Id")
        OR length(NEW."Id") <> 36
        OR substr(NEW."Id", 9, 1) <> '-'
        OR substr(NEW."Id", 14, 1) <> '-'
        OR substr(NEW."Id", 19, 1) <> '-'
        OR substr(NEW."Id", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'Entries.Id must be stored as an uppercase dashed 36-character identity.');
END;
