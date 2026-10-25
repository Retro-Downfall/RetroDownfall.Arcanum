CREATE INDEX IF NOT EXISTS "IX_InferenceRuns_Id_Norm" ON "InferenceRuns" (lower(replace("Id", '-', '')));
