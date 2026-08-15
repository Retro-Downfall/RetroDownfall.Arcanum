CREATE TABLE IF NOT EXISTS covenant_turn_receipt_aggregate (
    SessionId TEXT NOT NULL PRIMARY KEY,
    CoveredCount INTEGER NOT NULL CHECK (CoveredCount >= 0),
    EarliestCoveredAtUtc TEXT NULL,
    LatestCoveredAtUtc TEXT NULL,
    ConfirmedTokenTotal INTEGER NOT NULL CHECK (ConfirmedTokenTotal >= 0),
    ProposedTokenTotal INTEGER NOT NULL CHECK (ProposedTokenTotal >= 0),
    CompletedOutcomeCount INTEGER NOT NULL CHECK (CompletedOutcomeCount >= 0),
    FailedOutcomeCount INTEGER NOT NULL CHECK (FailedOutcomeCount >= 0),
    CancelledOutcomeCount INTEGER NOT NULL CHECK (CancelledOutcomeCount >= 0),
    InterruptedOutcomeCount INTEGER NOT NULL CHECK (InterruptedOutcomeCount >= 0),
    MutationTotal INTEGER NOT NULL CHECK (MutationTotal >= 0),
    -- Ordered and domain-separated: folding is order-sensitive, so the digest is evidence that this
    -- exact sequence of receipts folded, not merely that some set of them did.
    ChainDigest BLOB NOT NULL CHECK (length(ChainDigest) = 32),
    UpdatedAtUtc TEXT NOT NULL,
    -- An empty aggregate covers no time range; a nonempty one covers an ordered range. The outcome
    -- counts must also account for exactly the covered rows.
    CHECK (
        (CoveredCount = 0 AND EarliestCoveredAtUtc IS NULL AND LatestCoveredAtUtc IS NULL)
        OR (CoveredCount > 0
            AND EarliestCoveredAtUtc IS NOT NULL
            AND LatestCoveredAtUtc IS NOT NULL
            AND LatestCoveredAtUtc >= EarliestCoveredAtUtc)
    ),
    CHECK (
        CompletedOutcomeCount + FailedOutcomeCount + CancelledOutcomeCount + InterruptedOutcomeCount
            = CoveredCount
    )
);
