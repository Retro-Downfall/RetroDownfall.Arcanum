-- assistant_entry_finalizations.AssistantEntryId records the assistant Entry a finalization is terminal
-- for. Both of this table's writers hand the provider a raw Guid, which the value binder renders
-- uppercase unconditionally, so every row an installation holds is already canonical - which is why this
-- was the one guard version 5 could install before the sweep beside it had settled anything.
--
-- Insert only, and that is a finding about this table rather than a general rule:
-- assistant_entry_finalizations_guard_update aborts every update to it whatever the update changes, so an
-- update-time identity check here could never be reached. It is also why no sweep could move this column
-- even if one wanted to.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS assistant_entry_finalizations_AssistantEntryId_guard_identity_insert
BEFORE INSERT ON assistant_entry_finalizations
WHEN NEW.AssistantEntryId IS NOT NULL
    AND (NEW.AssistantEntryId <> upper(NEW.AssistantEntryId)
        OR length(NEW.AssistantEntryId) <> 36
        OR substr(NEW.AssistantEntryId, 9, 1) <> '-'
        OR substr(NEW.AssistantEntryId, 14, 1) <> '-'
        OR substr(NEW.AssistantEntryId, 19, 1) <> '-'
        OR substr(NEW.AssistantEntryId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'assistant_entry_finalizations.AssistantEntryId must be stored as an uppercase dashed 36-character identity.');
END;
