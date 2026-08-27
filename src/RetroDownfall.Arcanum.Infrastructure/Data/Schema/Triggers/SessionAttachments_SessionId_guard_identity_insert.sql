-- SessionAttachments."SessionId" is the Session an attachment belongs to. No foreign key protects the
-- relationship and Covenant, backup and retention components all compare it against Sessions."Id", which
-- is why this table is converted at all: a session-scoped backup that matched nothing here wrote an
-- archive whose attachment blobs were never copied and reported no failure.
--
-- Nullable - an attachment can exist before it is bound to a Session - so the guard says nothing about a
-- NULL.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS SessionAttachments_SessionId_guard_identity_insert
BEFORE INSERT ON "SessionAttachments"
WHEN NEW."SessionId" IS NOT NULL
    AND (NEW."SessionId" <> upper(NEW."SessionId")
        OR length(NEW."SessionId") <> 36
        OR substr(NEW."SessionId", 9, 1) <> '-'
        OR substr(NEW."SessionId", 14, 1) <> '-'
        OR substr(NEW."SessionId", 19, 1) <> '-'
        OR substr(NEW."SessionId", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'SessionAttachments.SessionId must be stored as an uppercase dashed 36-character identity.');
END;
