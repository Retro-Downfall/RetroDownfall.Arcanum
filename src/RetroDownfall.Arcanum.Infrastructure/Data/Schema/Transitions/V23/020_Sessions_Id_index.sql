CREATE INDEX IF NOT EXISTS "IX_Sessions_Id_Norm" ON "Sessions" (lower(replace("Id", '-', '')));
