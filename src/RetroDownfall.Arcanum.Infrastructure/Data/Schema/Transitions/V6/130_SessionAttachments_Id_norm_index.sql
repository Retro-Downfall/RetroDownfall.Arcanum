-- Same rule again; the head text is in Tables/SessionAttachments.sql.
CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Id_Norm"
  ON "SessionAttachments" (lower(replace("Id", '-', '')));
