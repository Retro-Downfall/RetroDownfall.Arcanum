-- The column definition text below is copied verbatim into the stored CREATE TABLE statement, so it
-- has to read character for character like the tail of Tables/saga_memories.sql. Quoting the column
-- name here and not there would install a table that no longer matches the file that declares it.
ALTER TABLE saga_memories ADD COLUMN ScopeKindCode INTEGER NOT NULL DEFAULT 0;
