-- artifact_sensitivity.SessionId records the Session a Covenant-derived artifact label belongs to,
-- written by the artifact sensitivity ledger. It is the one column of this family the version-5 sweep
-- does not count: it is left to this guard, which answers for every future write rather than for one
-- moment.
--
-- Nullable - an artifact need not belong to a Session - so the guard says nothing about a NULL.
--
-- Insert only, and that is a finding about this table rather than a general rule:
-- artifact_sensitivity_guard_update aborts every update to it whatever the update changes, because a
-- label is immutable evidence about one exact artifact revision. An update-time identity check here could
-- never be reached.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_SessionId_guard_identity_insert
BEFORE INSERT ON artifact_sensitivity
WHEN NEW.SessionId IS NOT NULL
    AND (NEW.SessionId <> upper(NEW.SessionId)
        OR length(NEW.SessionId) <> 36
        OR substr(NEW.SessionId, 9, 1) <> '-'
        OR substr(NEW.SessionId, 14, 1) <> '-'
        OR substr(NEW.SessionId, 19, 1) <> '-'
        OR substr(NEW.SessionId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'artifact_sensitivity.SessionId must be stored as an uppercase dashed 36-character identity.');
END;
