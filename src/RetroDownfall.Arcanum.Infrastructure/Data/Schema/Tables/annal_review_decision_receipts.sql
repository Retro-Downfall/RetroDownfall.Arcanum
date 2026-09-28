CREATE TABLE IF NOT EXISTS annal_review_decision_receipts (
    DecisionId TEXT NOT NULL PRIMARY KEY,
    ReviewEventSequence INTEGER NOT NULL REFERENCES annal_review_events(Sequence) ON DELETE CASCADE,
    RequestIdempotencyDigest BLOB NOT NULL CHECK (length(RequestIdempotencyDigest) = 32),
    DecisionCode INTEGER NOT NULL CHECK (DecisionCode = 1),
    ResponseReceiptDigest BLOB NOT NULL CHECK (length(ResponseReceiptDigest) = 32),
    UNIQUE (RequestIdempotencyDigest)
);

CREATE INDEX IF NOT EXISTS idx_annal_review_decision_receipts_event
    ON annal_review_decision_receipts(ReviewEventSequence);
