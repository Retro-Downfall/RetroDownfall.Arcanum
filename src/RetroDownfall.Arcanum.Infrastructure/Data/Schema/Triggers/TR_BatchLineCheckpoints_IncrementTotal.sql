CREATE TRIGGER IF NOT EXISTS "TR_BatchLineCheckpoints_IncrementTotal"
AFTER INSERT ON "BatchLineCheckpoints"
BEGIN
    UPDATE "Batches"
    SET "TotalRequestCount" = "TotalRequestCount" + 1
    WHERE "Id" = NEW."BatchId";
END;
