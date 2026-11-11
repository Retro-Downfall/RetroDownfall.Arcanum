CREATE TABLE IF NOT EXISTS guild_cohort_settlement_nodes (
    CohortId TEXT NOT NULL REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation>=1),
    AccountId TEXT NOT NULL,
    NodeJson TEXT NOT NULL CHECK(typeof(NodeJson)='text' AND length(CAST(NodeJson AS BLOB))<=65536 AND json_valid(NodeJson)),
    PRIMARY KEY(CohortId,Generation,AccountId)
);
