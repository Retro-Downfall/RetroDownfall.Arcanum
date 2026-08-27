-- The first of the write-time identity guards, and the step that makes the canonicalisation hold: once
-- every stored identity has one spelling, a comparison can be an exact indexed equality again, and the
-- only thing that keeps it that way is a refusal at the write. A guard fires on whatever produced the
-- row - the object-relational writer, a raw Guid handed to the provider, an interpolation, or SQL
-- nobody has written yet - which is exactly what a source scan of the writers can never cover.
--
-- Canonical means uppercase AND dashed AND 36 characters, not merely uppercase. A dash-free rendering
-- is already its own uppercase image, so a case-only check would pass Guid.ToString("N") silently; the
-- two columns that legitimately hold that form are single-writer and are deliberately guarded nowhere.
--
-- This table is guarded first because it needs no repair and cannot receive one. Both of its writers
-- hand the provider a raw Guid, which the SQLite value binder renders uppercase unconditionally, so
-- every row an installation holds is already canonical. And its own guard_update trigger refuses every
-- update whatever it changes, so no sweep could move these columns even if one wanted to - which makes
-- this the one identity guard with no ordering relationship to the data step beside it.
--
-- Named for the table rather than for a column, because it guards both of this table's identities in one
-- BEFORE INSERT rather than one trigger per column. The house carries both shapes already - compare
-- session_turn_claims_validate_update, which checks a dozen columns in one trigger. The remaining
-- identity guards should settle on one shape rather than inherit this one by accident.
CREATE TRIGGER IF NOT EXISTS assistant_entry_finalizations_guard_identity
BEFORE INSERT ON assistant_entry_finalizations
WHEN NEW.AssistantEntryId <> upper(NEW.AssistantEntryId)
    OR length(NEW.AssistantEntryId) <> 36
    OR substr(NEW.AssistantEntryId, 9, 1) <> '-'
    OR substr(NEW.AssistantEntryId, 14, 1) <> '-'
    OR substr(NEW.AssistantEntryId, 19, 1) <> '-'
    OR substr(NEW.AssistantEntryId, 24, 1) <> '-'
    OR NEW.SessionId <> upper(NEW.SessionId)
    OR length(NEW.SessionId) <> 36
    OR substr(NEW.SessionId, 9, 1) <> '-'
    OR substr(NEW.SessionId, 14, 1) <> '-'
    OR substr(NEW.SessionId, 19, 1) <> '-'
    OR substr(NEW.SessionId, 24, 1) <> '-'
BEGIN
    SELECT RAISE(ABORT, 'assistant_entry_finalizations identities must be stored as uppercase dashed identities of 36 characters.');
END;
