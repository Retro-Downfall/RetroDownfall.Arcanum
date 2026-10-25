CREATE INDEX IF NOT EXISTS "IX_UploadedFiles_Id_Norm" ON "UploadedFiles" (lower(replace("Id", '-', '')));
