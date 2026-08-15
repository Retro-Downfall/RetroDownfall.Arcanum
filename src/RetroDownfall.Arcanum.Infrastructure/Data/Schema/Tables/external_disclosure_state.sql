-- The installation-wide joined disclosure state, one row per destination class and revocability.
-- Subject aggregates join into it once, on terminal folding, using checked addition, Boolean OR,
-- timestamp maximum, and Bloom OR. Those operations only behave as a semilattice if the identity
-- element has exactly one encoding, which is what the shape check below pins down: an empty state
-- that could also be written as a lower bound would make "nothing was ever disclosed" and "at least
-- zero things were disclosed" indistinguishable, and a join would then quietly preserve the weaker
-- claim forever.
CREATE TABLE IF NOT EXISTS external_disclosure_state (
    -- CovenantEgressDestination, one through eight.
    DestinationCode INTEGER NOT NULL CHECK (DestinationCode IN (1, 2, 3, 4, 5, 6, 7, 8)),
    -- CovenantDisclosureRevocability: LocallyRevocable = 1, Nonrevocable = 2.
    RevocabilityCode INTEGER NOT NULL CHECK (RevocabilityCode IN (1, 2)),
    -- CovenantDisclosureCountKind: Exact = 1, LowerBound = 2.
    CountKindCode INTEGER NOT NULL CHECK (CountKindCode IN (1, 2)),
    EverOccurred INTEGER NOT NULL CHECK (EverOccurred IN (0, 1)),
    JoinedCount INTEGER NOT NULL CHECK (JoinedCount >= 0),
    -- Numeric, not the ISO-8601 text every other timestamp uses, because this column is joined by
    -- unsigned maximum and needs a zero identity element that no valid instant can collide with.
    MaxDisclosedAtUtcTicks INTEGER NOT NULL CHECK (MaxDisclosedAtUtcTicks >= 0),
    -- 256 bits of diagnostic evidence, merged by bitwise OR. It never authorizes anything.
    EvidenceBloom BLOB NOT NULL CHECK (length(EvidenceBloom) = 32),
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (DestinationCode, RevocabilityCode),
    -- The empty state has exactly one encoding and it is always Exact. Every nonempty state is
    -- positive in all four components at once, so an EverOccurred bit can never be paired with a
    -- zero count, an absent instant, or an empty Bloom that would make the disclosure look
    -- unevidenced.
    CHECK (
        (EverOccurred = 0
            AND CountKindCode = 1
            AND JoinedCount = 0
            AND MaxDisclosedAtUtcTicks = 0
            AND EvidenceBloom = zeroblob(32))
        OR (EverOccurred = 1
            AND JoinedCount >= 1
            AND MaxDisclosedAtUtcTicks > 0
            AND EvidenceBloom <> zeroblob(32))
    )
);
