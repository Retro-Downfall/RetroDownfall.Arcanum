-- Exactly one terminal aggregate; the report is reconstructed from immutable metadata only.
CREATE TABLE IF NOT EXISTS memory_evaluation_reports (
    EvaluationId TEXT NOT NULL PRIMARY KEY REFERENCES memory_evaluations(EvaluationId) ON DELETE CASCADE,
    CompletedAtUtc TEXT NOT NULL CHECK(length(CompletedAtUtc) = 28 AND substr(CompletedAtUtc,28,1) = 'Z'),
    StateCode INTEGER NOT NULL CHECK(StateCode IN (3,4,5)),
    OutcomeCode INTEGER NOT NULL CHECK(OutcomeCode BETWEEN 1 AND 4),
    ReasonCode INTEGER NOT NULL CHECK(ReasonCode BETWEEN 0 AND 15),
    AggregateJson TEXT NOT NULL CHECK(json_valid(AggregateJson) AND length(CAST(AggregateJson AS BLOB)) BETWEEN 1 AND 8192),
    InputTokens INTEGER NULL CHECK(InputTokens IS NULL OR InputTokens >= 0),
    OutputTokens INTEGER NULL CHECK(OutputTokens IS NULL OR OutputTokens >= 0),
    Cost TEXT NULL,
    ElapsedMilliseconds INTEGER NOT NULL CHECK(ElapsedMilliseconds >= 0),
    ReportDigest BLOB NOT NULL CHECK(length(ReportDigest) = 32)
);
