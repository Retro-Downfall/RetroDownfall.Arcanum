-- The same rewrite for RunId, and it is here for column uniformity rather than for a join. RunId's only
-- production writer is SubagentRunner, which mints a fresh Guid for the child run and stores it in no
-- other table, so no cross-table comparison depends on this spelling today. What does depend on it is
-- that one column holds one spelling: the store writes uppercase dashed here now, and a column holding
-- both eras is a column no reader can compare against without normalizing, whether or not a join
-- exists yet.
UPDATE "LongRunningOperations"
SET "RunId" =
    upper(
        substr("RunId", 1, 8) || '-' || substr("RunId", 9, 4) || '-'
        || substr("RunId", 13, 4) || '-' || substr("RunId", 17, 4) || '-'
        || substr("RunId", 21, 12))
WHERE "RunId" IS NOT NULL
  AND length("RunId") = 32
  AND instr("RunId", '-') = 0;
