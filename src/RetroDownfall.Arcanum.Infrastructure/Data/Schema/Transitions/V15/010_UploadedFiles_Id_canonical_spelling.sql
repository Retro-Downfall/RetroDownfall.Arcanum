-- UploadedFiles.Id is the identity every batch file role names.
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
--
-- OR IGNORE: should two rows ever differ only by case, renaming the second would collide with the first on
-- the primary key and abort the whole transition. The first keeps its spelling, the second keeps the
-- non-canonical one. UploadedFileRepository still finds a lowercase dashed or lowercase dash-free leftover,
-- because it looks a file up by those spellings as well as the canonical one, and its delete removes both rows
-- together; a mixed-case leftover is reachable only by the retention sweep, which normalizes both sides.
UPDATE OR IGNORE "UploadedFiles"
SET "Id" = upper(
        CASE
            WHEN length("Id") = 32 AND instr("Id", '-') = 0
                THEN substr("Id", 1, 8) || '-' || substr("Id", 9, 4) || '-'
                    || substr("Id", 13, 4) || '-' || substr("Id", 17, 4) || '-'
                    || substr("Id", 21, 12)
            ELSE "Id"
        END)
WHERE "Id" <> upper("Id")
   OR (length("Id") = 32 AND instr("Id", '-') = 0);
