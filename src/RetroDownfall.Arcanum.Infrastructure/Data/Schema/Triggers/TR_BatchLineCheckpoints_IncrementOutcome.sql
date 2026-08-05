CREATE TRIGGER IF NOT EXISTS "TR_BatchLineCheckpoints_IncrementOutcome"
AFTER UPDATE OF "State", "Outcome" ON "BatchLineCheckpoints"
WHEN OLD."State" = 0 AND NEW."State" = 1
BEGIN
    UPDATE "Batches"
    SET "CompletedRequestCount" = "CompletedRequestCount" + CASE WHEN NEW."Outcome" = 0 THEN 1 ELSE 0 END,
        "FailedRequestCount" = "FailedRequestCount" + CASE WHEN NEW."Outcome" = 1 THEN 1 ELSE 0 END
    WHERE "Id" = NEW."BatchId";
END;
