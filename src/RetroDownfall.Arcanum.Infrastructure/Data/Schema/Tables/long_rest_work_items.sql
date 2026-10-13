CREATE TABLE IF NOT EXISTS long_rest_work_items (
    WorkItemId TEXT NOT NULL PRIMARY KEY,
    WorkDigest BLOB NOT NULL UNIQUE CHECK (length(WorkDigest) = 32),
    GenerationId TEXT NOT NULL REFERENCES long_rest_discovery_generations(GenerationId) ON DELETE CASCADE,
    OperationId TEXT NOT NULL REFERENCES LongRunningOperations(Id) ON DELETE CASCADE,
    KindCode INTEGER NOT NULL CHECK (KindCode BETWEEN 1 AND 3),
    ItemId TEXT NOT NULL CHECK (length(CAST(ItemId AS BLOB)) BETWEEN 1 AND 1024),
    SourceDigest BLOB NOT NULL CHECK (length(SourceDigest) = 32),
    RecipeDigest BLOB NOT NULL CHECK (length(RecipeDigest) = 32),
    Revision INTEGER NOT NULL CHECK (typeof(Revision) = 'integer' AND Revision >= 0),
    DispositionCode INTEGER NOT NULL CHECK (DispositionCode BETWEEN 1 AND 4),
    ReceiptId TEXT NULL REFERENCES long_rest_receipts(ReceiptId) ON DELETE CASCADE,
    UNIQUE (GenerationId, KindCode, ItemId)
);
