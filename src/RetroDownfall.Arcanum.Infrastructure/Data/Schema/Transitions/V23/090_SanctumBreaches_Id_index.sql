CREATE INDEX IF NOT EXISTS "IX_SanctumBreaches_Id_Norm" ON "SanctumBreaches" (lower(replace("Id", '-', '')));
