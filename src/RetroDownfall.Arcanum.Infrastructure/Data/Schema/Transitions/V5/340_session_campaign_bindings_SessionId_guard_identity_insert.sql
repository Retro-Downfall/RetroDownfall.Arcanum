-- session_campaign_bindings.SessionId is the one governed identity column whose canonicality the schema
-- already enforces by other means: it is declared REFERENCES "Sessions"("Id"), and foreign keys are both
-- set and verified on every connection, so it cannot hold a spelling its parent does not hold. The guard
-- is here anyway, and it earns its place: a foreign key says "the same as the parent", not "canonical",
-- so it would have been equally satisfied by a lowercase pair on an installation whose Sessions had been
-- hand-edited. This says what the column must hold rather than only who it must agree with, and it names
-- the column when it refuses.
--
-- The writer that made this column a defect rather than a rule bound a bare ToString() while the parent
-- was written by the object-relational writer, so every Campaign-bound and global-only Session creation
-- failed the foreign key and no Session could be created through the turn-begin path at all.
--
-- Insert only, and for a third distinct reason - not because the table refuses every update, which it
-- does not. session_campaign_bindings_guard_update aborts any update that changes SessionId, so the only
-- update BEFORE UPDATE OF "SessionId" could ever see is one setting the column to the value it already
-- holds. There is no reachable write for an update guard to judge.
--
-- Canonical means uppercase AND dashed AND 36 characters, and each of those is a separate way to be
-- wrong: a dash-free rendering is already its own uppercase image, so a case-only check would pass
-- Guid.ToString("N") in silence. See Sessions_Id_guard_identity_insert for why this family is one
-- trigger per column, what the abort message has to name, and why the sweep that ships in the same
-- version cannot trip it.
CREATE TRIGGER IF NOT EXISTS session_campaign_bindings_SessionId_guard_identity_insert
BEFORE INSERT ON session_campaign_bindings
WHEN NEW.SessionId IS NOT NULL
    AND (NEW.SessionId <> upper(NEW.SessionId)
        OR length(NEW.SessionId) <> 36
        OR substr(NEW.SessionId, 9, 1) <> '-'
        OR substr(NEW.SessionId, 14, 1) <> '-'
        OR substr(NEW.SessionId, 19, 1) <> '-'
        OR substr(NEW.SessionId, 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'session_campaign_bindings.SessionId must be stored as an uppercase dashed 36-character identity.');
END;
