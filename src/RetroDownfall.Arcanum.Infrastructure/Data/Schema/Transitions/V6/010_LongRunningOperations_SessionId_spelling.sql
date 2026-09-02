-- LongRunningOperations.SessionId names a row in Sessions, whose Id every writer renders uppercase
-- dashed and whose write-time guard has refused anything else since version 5. This column was written
-- through the store's own "N" rendering until W3b-5 switched it, so an installation holds both eras:
-- rows written before that switch in the dash-free spelling, rows written after it in the spelling
-- Sessions actually holds. A join written the obvious way - SessionId = Sessions.Id, with no
-- lower(replace(...)) on either side - therefore matches every new row and silently misses every old
-- one, which is the failure the switch was meant to end and did not.
--
-- The predicate is the shape of the value rather than a guess about when it was written. Exactly 32
-- characters with no dash in them is the dash-free rendering and cannot be anything else: the canonical
-- form is 36 characters and carries four. upper() is applied with the splice because the dash-free
-- rendering has a lowercase era of its own, and re-running this statement against an already-canonical
-- row is a no-op rather than a corruption - the WHERE clause excludes it.
UPDATE "LongRunningOperations"
SET "SessionId" =
    upper(
        substr("SessionId", 1, 8) || '-' || substr("SessionId", 9, 4) || '-'
        || substr("SessionId", 13, 4) || '-' || substr("SessionId", 17, 4) || '-'
        || substr("SessionId", 21, 12))
WHERE "SessionId" IS NOT NULL
  AND length("SessionId") = 32
  AND instr("SessionId", '-') = 0;
