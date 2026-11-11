CREATE TABLE IF NOT EXISTS guild_cohort_ledger (
    Id TEXT NOT NULL PRIMARY KEY,
    CohortId TEXT NOT NULL REFERENCES guild_cohorts(Id) ON DELETE RESTRICT,
    Sequence INTEGER NOT NULL CHECK(typeof(Sequence)='integer' AND Sequence>=1),
    AuthorQuestId TEXT NULL,
    AuthorMemberId TEXT NULL,
    OperatorPrincipalId TEXT NULL,
    AuthorityCode INTEGER NOT NULL CHECK(AuthorityCode IN(1,2,3)),
    KindCode INTEGER NOT NULL CHECK(KindCode IN(1,2,3)),
    SubjectId TEXT NULL,
    CorrectsNoteId TEXT NULL,
    ContentId TEXT NOT NULL UNIQUE,
    SessionId TEXT NULL,
    CampaignId TEXT NULL,
    Digest BLOB NOT NULL CHECK(typeof(Digest)='blob' AND length(Digest)=32),
    ByteLength INTEGER NOT NULL CHECK(typeof(ByteLength)='integer' AND ByteLength BETWEEN 1 AND 65536),
    ActorKey TEXT NOT NULL CHECK(length(CAST(ActorKey AS BLOB)) BETWEEN 1 AND 128),
    IdempotencyKey TEXT NOT NULL,
    RequestDigest BLOB NOT NULL CHECK(typeof(RequestDigest)='blob' AND length(RequestDigest)=32),
    ReceiptId TEXT NOT NULL UNIQUE,
    AcceptedRevision INTEGER NOT NULL CHECK(AcceptedRevision>=1),
    CreatedAtUtc TEXT NOT NULL,
    UNIQUE(CohortId,Sequence),
    UNIQUE(CohortId,ActorKey,IdempotencyKey),
    CHECK((AuthorityCode=1 AND AuthorQuestId IS NULL AND AuthorMemberId IS NULL AND OperatorPrincipalId IS NOT NULL)
        OR (AuthorityCode=2 AND AuthorQuestId IS NOT NULL AND AuthorMemberId IS NULL AND OperatorPrincipalId IS NULL)
        OR (AuthorityCode=3 AND AuthorQuestId IS NOT NULL AND AuthorMemberId IS NOT NULL AND OperatorPrincipalId IS NULL)),
    CHECK((KindCode=3 AND CorrectsNoteId IS NOT NULL) OR (KindCode IN(1,2) AND CorrectsNoteId IS NULL))
);
