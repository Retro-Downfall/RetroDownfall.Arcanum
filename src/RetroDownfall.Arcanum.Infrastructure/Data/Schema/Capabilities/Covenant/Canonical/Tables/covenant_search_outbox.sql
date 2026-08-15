CREATE TABLE IF NOT EXISTS covenant_search_outbox (
    SearchSequence INTEGER NOT NULL CHECK (SearchSequence > 0),
    Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
    SearchRowId INTEGER NOT NULL CHECK (SearchRowId > 0),
    EntryId TEXT NOT NULL,
    LaneCode INTEGER NOT NULL CHECK (LaneCode IN (1, 2)),
    -- Null means "this head is absent": a deletion delta. The outbox carries identities and this one
    -- nullable pointer and nothing else, so it never holds Covenant text.
    DesiredVersionId TEXT NULL,
    PRIMARY KEY (SearchSequence, Ordinal)
);

CREATE INDEX IF NOT EXISTS idx_covenant_search_outbox_row
    ON covenant_search_outbox(SearchRowId);
