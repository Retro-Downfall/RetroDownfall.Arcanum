-- The folded form of a subject's receipts, one row per destination class and revocability. The key
-- space is closed at eight destinations by two revocabilities, so a subject can never hold more than
-- sixteen aggregate rows no matter how many receipts folded into it. That bound is what lets
-- compaction reclaim an unbounded tail without an unbounded index.
CREATE TABLE IF NOT EXISTS disclosure_subject_aggregates (
    OriginInstallationId TEXT NOT NULL CHECK (length(OriginInstallationId) > 0),
    -- CovenantDisclosureSubjectKind: Turn = 1, Operation = 2.
    SubjectKind INTEGER NOT NULL CHECK (SubjectKind IN (1, 2)),
    SubjectId TEXT NOT NULL CHECK (length(SubjectId) > 0),
    -- CovenantEgressDestination, one through eight.
    DestinationCode INTEGER NOT NULL CHECK (DestinationCode IN (1, 2, 3, 4, 5, 6, 7, 8)),
    -- CovenantDisclosureRevocability: LocallyRevocable = 1, Nonrevocable = 2.
    RevocabilityCode INTEGER NOT NULL CHECK (RevocabilityCode IN (1, 2)),
    -- CovenantDisclosureCountKind: Exact = 1, LowerBound = 2. Once a fold loses exactness it can
    -- never be regained, so the kind travels with the count rather than being inferred later.
    CountKindCode INTEGER NOT NULL CHECK (CountKindCode IN (1, 2)),
    FoldedCount INTEGER NOT NULL CHECK (FoldedCount >= 1),
    EverOccurred INTEGER NOT NULL CHECK (EverOccurred IN (0, 1)),
    -- Numeric, not the ISO-8601 text every other timestamp uses, because this column is joined by
    -- unsigned maximum and needs a zero identity element that no valid instant can collide with.
    MaxDisclosedAtUtcTicks INTEGER NOT NULL CHECK (MaxDisclosedAtUtcTicks > 0),
    -- 256 bits of diagnostic evidence. It never authorizes replay, read, or erasure; a set bit is a
    -- hint that a receipt digest was folded here, nothing more.
    EvidenceBloom BLOB NOT NULL CHECK (length(EvidenceBloom) = 32),
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (OriginInstallationId, SubjectKind, SubjectId, DestinationCode, RevocabilityCode),
    -- An aggregate row exists only because at least one receipt folded into it. The empty shape
    -- belongs to external_disclosure_state, which must be able to say "nothing happened here"; a
    -- subject aggregate that said the same thing would just be a row that should not exist.
    CHECK (EverOccurred = 1)
);
