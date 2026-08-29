-- assistant_entry_finalizations.SessionId carries the Session whose turn was finalized. Both of this
-- table's writers hand the provider a raw Guid, which the value binder renders uppercase unconditionally.
--
-- Insert only, for the same reason its sibling is: assistant_entry_finalizations_guard_update aborts
-- every update to this table, so an update-time identity check could never be reached.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS assistant_entry_finalizations_SessionId_guard_identity_insert
BEFORE INSERT ON assistant_entry_finalizations
WHEN NEW.SessionId IS NOT NULL
    AND (NEW.SessionId <> upper(NEW.SessionId)
        OR length(NEW.SessionId) <> 36
        OR substr(NEW.SessionId, 9, 1) <> '-'
        OR substr(NEW.SessionId, 14, 1) <> '-'
        OR substr(NEW.SessionId, 19, 1) <> '-'
        OR substr(NEW.SessionId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'assistant_entry_finalizations.SessionId must be stored as an uppercase dashed 36-character identity.');
END;
