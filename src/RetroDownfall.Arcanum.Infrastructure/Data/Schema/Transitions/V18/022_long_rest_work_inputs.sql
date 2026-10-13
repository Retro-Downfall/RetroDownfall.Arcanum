CREATE TABLE IF NOT EXISTS long_rest_work_inputs (
    WorkItemId TEXT NOT NULL REFERENCES long_rest_work_items(WorkItemId) ON DELETE CASCADE,
    Ordinal INTEGER NOT NULL CHECK (Ordinal BETWEEN 1 AND 16),
    MemoryId TEXT NOT NULL,
    VersionId TEXT NOT NULL,
    SourceEvidenceJson TEXT NOT NULL CHECK (length(CAST(SourceEvidenceJson AS BLOB)) BETWEEN 2 AND 1048576),
    PRIMARY KEY (WorkItemId, Ordinal),
    UNIQUE (WorkItemId, MemoryId),
    UNIQUE (WorkItemId, VersionId)
);
