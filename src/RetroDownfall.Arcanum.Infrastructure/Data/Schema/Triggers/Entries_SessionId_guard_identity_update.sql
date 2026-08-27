-- Entries."SessionId" carries the Session an Entry belongs to, on the largest table in the database and
-- on the read path that runs once per turn for every user. It is the comparison whose normalisation cost
-- all three SessionId-led indexes and started this work; this guard is what lets it be an exact indexed
-- equality again.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_update for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS Entries_SessionId_guard_identity_update
BEFORE UPDATE OF "SessionId" ON "Entries"
WHEN NEW."SessionId" IS NOT NULL
    AND (NEW."SessionId" <> upper(NEW."SessionId")
        OR length(NEW."SessionId") <> 36
        OR substr(NEW."SessionId", 9, 1) <> '-'
        OR substr(NEW."SessionId", 14, 1) <> '-'
        OR substr(NEW."SessionId", 19, 1) <> '-'
        OR substr(NEW."SessionId", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'Entries.SessionId must be stored as an uppercase dashed 36-character identity.');
END;
