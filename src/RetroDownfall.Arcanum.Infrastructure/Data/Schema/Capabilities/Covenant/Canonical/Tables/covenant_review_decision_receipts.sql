CREATE TABLE IF NOT EXISTS covenant_review_decision_receipts (
    DecisionId TEXT NOT NULL PRIMARY KEY,
    DatasetGeneration BLOB NOT NULL CHECK (length(DatasetGeneration) = 16),
    ReviewEventSequence INTEGER NOT NULL REFERENCES covenant_review_events(Sequence) ON DELETE CASCADE,
    RequestIdempotencyDigest BLOB NOT NULL CHECK (length(RequestIdempotencyDigest) = 32),
    DecisionCode INTEGER NOT NULL CHECK (DecisionCode = 1),
    ResponseReceiptDigest BLOB NOT NULL CHECK (length(ResponseReceiptDigest) = 32),
    UNIQUE (RequestIdempotencyDigest)
);

CREATE INDEX IF NOT EXISTS idx_covenant_review_decision_receipts_event
    ON covenant_review_decision_receipts(ReviewEventSequence);
