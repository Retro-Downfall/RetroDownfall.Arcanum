-- SessionAttachments."Id" is the one identity in this schema that version 5 moves in place rather than
-- merely verifying: every row held the minority spelling, written by an attachment store that rendered a
-- bare ToString(), and columns in other tables name it. It is written by the attachment store,
-- the protected artifact transfer store and the backup session importer, all three of which now render
-- the canonical form.
--
-- The sweep that moves it writes upper() of an already-dashed 36-character value into this very column,
-- and passes the update guard below for exactly that reason.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS SessionAttachments_Id_guard_identity_insert
BEFORE INSERT ON "SessionAttachments"
WHEN NEW."Id" IS NOT NULL
    AND (NEW."Id" <> upper(NEW."Id")
        OR length(NEW."Id") <> 36
        OR substr(NEW."Id", 9, 1) <> '-'
        OR substr(NEW."Id", 14, 1) <> '-'
        OR substr(NEW."Id", 19, 1) <> '-'
        OR substr(NEW."Id", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'SessionAttachments.Id must be stored as an uppercase dashed 36-character identity.');
END;
