CREATE TABLE IF NOT EXISTS long_rest_adjudications (
    WorkItemId TEXT NOT NULL PRIMARY KEY REFERENCES long_rest_work_items(WorkItemId) ON DELETE CASCADE,
    WorkDigest BLOB NOT NULL CHECK (length(WorkDigest) = 32),
    DecisionKindCode INTEGER NOT NULL CHECK (DecisionKindCode BETWEEN 1 AND 3),
    SurvivorVersionId TEXT NULL,
    PayloadDigest BLOB NOT NULL CHECK (length(PayloadDigest) = 32),
    EvidenceJson TEXT NOT NULL CHECK (length(CAST(EvidenceJson AS BLOB)) BETWEEN 2 AND 1048576),
    CreatedAtUtc TEXT NOT NULL
);
