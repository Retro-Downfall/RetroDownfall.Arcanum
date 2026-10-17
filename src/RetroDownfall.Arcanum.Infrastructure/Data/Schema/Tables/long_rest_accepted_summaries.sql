CREATE TABLE IF NOT EXISTS long_rest_accepted_summaries (
    WorkItemId TEXT NOT NULL PRIMARY KEY REFERENCES long_rest_work_items(WorkItemId) ON DELETE CASCADE,
    WorkDigest BLOB NOT NULL CHECK (length(WorkDigest) = 32),
    GenerationId TEXT NOT NULL REFERENCES long_rest_discovery_generations(GenerationId) ON DELETE CASCADE,
    NodeId TEXT NOT NULL UNIQUE,
    MembershipDigest BLOB NOT NULL CHECK (length(MembershipDigest) = 32),
    SourceDigest BLOB NOT NULL CHECK (length(SourceDigest) = 32),
    RecipeDigest BLOB NOT NULL CHECK (length(RecipeDigest) = 32),
    PayloadDigest BLOB NOT NULL CHECK (length(PayloadDigest) = 32),
    Summary TEXT NOT NULL CHECK (length(CAST(Summary AS BLOB)) BETWEEN 1 AND 8192),
    SummaryDigest BLOB NOT NULL CHECK (length(SummaryDigest) = 32),
    EvidenceJson TEXT NOT NULL CHECK (length(CAST(EvidenceJson AS BLOB)) BETWEEN 2 AND 1048576),
    CreatedAtUtc TEXT NOT NULL
);

-- The label guard proves that no accepted summary still belongs to the label's node by the normalized identity: lower(replace(NodeId, '-', '')),
-- in the exact shape the predicate has.
CREATE INDEX IF NOT EXISTS idx_long_rest_accepted_summaries_node_norm
    ON long_rest_accepted_summaries(lower(replace(NodeId, '-', '')));
