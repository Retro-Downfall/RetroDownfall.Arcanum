CREATE TABLE IF NOT EXISTS guild_distribution_notifications (
    EnvelopeId TEXT NOT NULL PRIMARY KEY REFERENCES guild_envelopes(Id) ON DELETE RESTRICT,
    CohortId TEXT NOT NULL REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    TypeCode INTEGER NOT NULL CHECK(typeof(TypeCode)='integer' AND TypeCode BETWEEN 1 AND 7),
    ObjectId TEXT NOT NULL CHECK(typeof(ObjectId)='text' AND length(CAST(ObjectId AS BLOB))=36),
    ObjectSequence INTEGER NOT NULL CHECK(typeof(ObjectSequence)='integer' AND ObjectSequence>=1)
);
