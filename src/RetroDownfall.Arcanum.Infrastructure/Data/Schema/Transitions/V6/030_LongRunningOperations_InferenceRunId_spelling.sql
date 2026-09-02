-- InferenceRunId completes the set the store now writes uppercase dashed. No production caller supplies
-- it - LongRunningOperationCreateRequest defaults it to null and nothing overrides that default - so
-- this statement is expected to match zero rows on every installation, and it is declared anyway so the
-- column cannot become the one place a dash-free spelling survives the moment a caller appears.
UPDATE "LongRunningOperations"
SET "InferenceRunId" =
    upper(
        substr("InferenceRunId", 1, 8) || '-' || substr("InferenceRunId", 9, 4) || '-'
        || substr("InferenceRunId", 13, 4) || '-' || substr("InferenceRunId", 17, 4) || '-'
        || substr("InferenceRunId", 21, 12))
WHERE "InferenceRunId" IS NOT NULL
  AND length("InferenceRunId") = 32
  AND instr("InferenceRunId", '-') = 0;
