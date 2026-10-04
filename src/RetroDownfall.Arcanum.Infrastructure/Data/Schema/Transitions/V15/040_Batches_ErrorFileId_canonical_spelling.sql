-- Batches.ErrorFileId names a row in UploadedFiles, whose Id is settled to the canonical spelling by the
-- statement before the first of these.
-- Canonical means uppercase, dashed and 36 characters: the spelling EF writes for a Guid and the one
-- GrimoireEntitySql.Format renders. Before this version the repositories wrote the file identities in
-- lowercase dashed form while their lookups wrapped the column in lower(replace(col, '-', '')) to cope with
-- the spellings that had ever been written, and SQLite cannot answer a function-wrapped column from an
-- ordinary index, so every delete and every reference check was a scan.
--
-- Two stored shapes are non-canonical and both are repaired by the shape of the value, not by a guess about
-- when it was written: lowercase (or mixed-case) dashed text, which upper() settles, and the 32-character
-- dash-free rendering, which is spliced into dashes first. Re-running the statement against a canonical row
-- is a no-op because the WHERE clause excludes it.
UPDATE "Batches"
SET "ErrorFileId" = upper(
        CASE
            WHEN length("ErrorFileId") = 32 AND instr("ErrorFileId", '-') = 0
                THEN substr("ErrorFileId", 1, 8) || '-' || substr("ErrorFileId", 9, 4) || '-'
                    || substr("ErrorFileId", 13, 4) || '-' || substr("ErrorFileId", 17, 4) || '-'
                    || substr("ErrorFileId", 21, 12)
            ELSE "ErrorFileId"
        END)
WHERE "ErrorFileId" IS NOT NULL
  AND ("ErrorFileId" <> upper("ErrorFileId")
       OR (length("ErrorFileId") = 32 AND instr("ErrorFileId", '-') = 0));
