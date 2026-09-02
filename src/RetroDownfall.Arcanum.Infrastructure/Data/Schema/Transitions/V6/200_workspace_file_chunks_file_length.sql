-- The column definition text below is copied verbatim into the stored CREATE TABLE statement, so it
-- has to read character for character like the tail of Tables/workspace_file_chunks.sql.
ALTER TABLE workspace_file_chunks ADD COLUMN FileLength INTEGER NOT NULL DEFAULT -1;
