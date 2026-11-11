CREATE TABLE IF NOT EXISTS guild_cohort_result_observations (
    QuestId TEXT NOT NULL PRIMARY KEY REFERENCES guild_quests(Id) ON DELETE RESTRICT,
    ResultId TEXT NOT NULL,
    ProofDigest BLOB NOT NULL CHECK(typeof(ProofDigest)='blob' AND length(ProofDigest)=32)
);
