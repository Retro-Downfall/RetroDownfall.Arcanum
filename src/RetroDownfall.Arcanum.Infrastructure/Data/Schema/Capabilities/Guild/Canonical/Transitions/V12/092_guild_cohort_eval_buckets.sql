CREATE TABLE IF NOT EXISTS guild_cohort_eval_buckets (
    CohortId TEXT NOT NULL REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    Generation INTEGER NOT NULL CHECK(typeof(Generation)='integer' AND Generation>=1),
    BucketId INTEGER NOT NULL CHECK(typeof(BucketId)='integer' AND BucketId>=1),
    AgreementDigest BLOB NOT NULL CHECK(typeof(AgreementDigest)='blob' AND length(AgreementDigest)=32),
    RepresentativeContentId TEXT NOT NULL,
    VoteCount INTEGER NOT NULL CHECK(typeof(VoteCount)='integer' AND VoteCount>=1),
    PRIMARY KEY(CohortId,Generation,BucketId)
);
