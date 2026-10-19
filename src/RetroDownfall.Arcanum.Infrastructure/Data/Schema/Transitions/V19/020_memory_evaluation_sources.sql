-- Sources are inserted before their deferred parent, then sealed by root insertion.
CREATE TABLE IF NOT EXISTS memory_evaluation_sources (
    EvaluationId TEXT NOT NULL,
    Ordinal INTEGER NOT NULL CHECK(Ordinal BETWEEN 0 AND 127),
    StoreCode INTEGER NOT NULL CHECK(StoreCode IN (1,2,3)),
    RoleCode INTEGER NOT NULL CHECK(RoleCode IN (1,2)),
    SubjectId TEXT NOT NULL CHECK(length(CAST(SubjectId AS BLOB)) BETWEEN 1 AND 65536),
    VersionId TEXT NOT NULL CHECK(length(CAST(VersionId AS BLOB)) BETWEEN 1 AND 65536),
    CoordinateJson TEXT NOT NULL CHECK(json_valid(CoordinateJson) AND length(CAST(CoordinateJson AS BLOB)) BETWEEN 1 AND 65536),
    PRIMARY KEY(EvaluationId,Ordinal),
    UNIQUE(EvaluationId,StoreCode,SubjectId,VersionId,RoleCode),
    FOREIGN KEY(EvaluationId) REFERENCES memory_evaluations(EvaluationId) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX IF NOT EXISTS ix_memory_evaluation_sources_version ON memory_evaluation_sources(StoreCode,VersionId);
