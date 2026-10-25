CREATE INDEX IF NOT EXISTS "IX_IdempotencyClaims_Id_Norm" ON "IdempotencyClaims" (lower(replace("Id", '-', '')));
