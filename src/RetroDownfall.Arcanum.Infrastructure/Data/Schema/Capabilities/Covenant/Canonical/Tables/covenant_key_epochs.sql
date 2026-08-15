CREATE TABLE IF NOT EXISTS covenant_key_epochs (
    -- Keyed by normalized key alone, across every Global and Campaign lane. Rows are therefore
    -- proportional to retained canonical keys rather than to historical Campaign churn, which keeps
    -- Global effect validation O(1) without an unbounded tombstone table or a scan under the write
    -- lock.
    NormalizedKey TEXT NOT NULL PRIMARY KEY CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
    KeyEpoch INTEGER NOT NULL CHECK (KeyEpoch > 0),
    UpdatedAtUtc TEXT NOT NULL
);
