CREATE INDEX IF NOT EXISTS idx_covenant_review_events_erasure_entry
    ON covenant_review_events(lower(replace(EntryId, '-', '')));
