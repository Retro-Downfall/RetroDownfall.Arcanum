-- session_sensitivity_state.SessionId is written by the artifact sensitivity ledger, which resolves the
-- spelling Sessions."Id" already holds rather than rendering its own - the column declares
-- REFERENCES "Sessions" ("Id") and SQLite resolves a foreign key by byte equality. With Sessions."Id"
-- guarded above, what that resolver returns is canonical or there is no Session at all.
--
-- The ledger's fold is an upsert whose ON CONFLICT DO UPDATE never names this column, so the update guard
-- below does not fire on it.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS session_sensitivity_state_SessionId_guard_identity_insert
BEFORE INSERT ON session_sensitivity_state
WHEN NEW.SessionId IS NOT NULL
    AND (NEW.SessionId <> upper(NEW.SessionId)
        OR length(NEW.SessionId) <> 36
        OR substr(NEW.SessionId, 9, 1) <> '-'
        OR substr(NEW.SessionId, 14, 1) <> '-'
        OR substr(NEW.SessionId, 19, 1) <> '-'
        OR substr(NEW.SessionId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'session_sensitivity_state.SessionId must be stored as an uppercase dashed 36-character identity.');
END;
