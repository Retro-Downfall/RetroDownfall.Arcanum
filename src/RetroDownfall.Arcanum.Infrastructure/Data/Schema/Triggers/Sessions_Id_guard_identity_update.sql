-- The update half of the identity guards, and BEFORE UPDATE OF <column> rather than BEFORE UPDATE is
-- load-bearing rather than a narrowing for speed.
--
-- A guard refuses the value being written. On an UPDATE the values being written are the ones named in
-- the SET clause, and a trigger that also judged the columns the statement leaves alone would refuse a
-- row for data that was already there. That is not hypothetical: the version-5 step installs these
-- triggers before it runs its own sweep, and that sweep repairs one identity column of a row at a time -
-- SessionAttachments."Id" in a bounded page, then "SessionId" and "EntryId" against the identities they
-- name. A guard that judged all three on every update would abort the migration on any installation that
-- has ever held an attachment, and every retry of it, leaving the tier permanently unable to reach head.
-- UPDATE OF removes that by construction rather than by an OLD-value comparison a copier could omit.
--
-- The sweep's own writes pass for a second reason, and both are needed: it only ever selects a row whose
-- shape is already canonical, so the value it writes is upper() of a 36-character dashed string. See
-- IdentitySpellingBackfill.CanonicalShapeClause.
--
-- Where a table already refuses every update whatever it changes - assistant_entry_finalizations and
-- artifact_sensitivity both do - no update guard of this family is added, because it could never be
-- reached. That is a per-table finding rather than a general rule.
--
-- Nothing in the shipped code updates Sessions."Id" and nothing should: eight of its fourteen
-- foreign-key children refuse the write by trigger, four of them unconditionally, which is why version 5
-- verifies this column and never moves it.
CREATE TRIGGER IF NOT EXISTS Sessions_Id_guard_identity_update
BEFORE UPDATE OF "Id" ON "Sessions"
WHEN NEW."Id" IS NOT NULL
    AND (NEW."Id" <> upper(NEW."Id")
        OR length(NEW."Id") <> 36
        OR substr(NEW."Id", 9, 1) <> '-'
        OR substr(NEW."Id", 14, 1) <> '-'
        OR substr(NEW."Id", 19, 1) <> '-'
        OR substr(NEW."Id", 24, 1) <> '-')
BEGIN
    SELECT RAISE(ABORT, 'Sessions.Id must be stored as an uppercase dashed 36-character identity.');
END;
