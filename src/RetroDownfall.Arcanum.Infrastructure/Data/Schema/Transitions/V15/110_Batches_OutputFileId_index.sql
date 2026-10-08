-- The statement text is character for character the one in Tables/Batches.sql. The installer compares an
-- installed index's stored DDL with the head file's, normalized, so a transition that phrased the same
-- index differently would report DefinitionDrift on every evolved installation and on none of the fresh
-- ones.
CREATE INDEX IF NOT EXISTS "IX_Batches_OutputFileId" ON "Batches" ("OutputFileId");
