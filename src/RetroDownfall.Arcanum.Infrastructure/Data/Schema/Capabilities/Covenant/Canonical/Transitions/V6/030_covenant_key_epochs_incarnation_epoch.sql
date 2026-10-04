-- The table declares no table constraint, so SQLite splices the added column in front of the closing
-- parenthesis, which is where the head file spells it. The default fills every existing row with 0
-- until the backfill below replaces it.
ALTER TABLE covenant_key_epochs ADD COLUMN IncarnationEpoch INTEGER NOT NULL DEFAULT 0 CHECK (IncarnationEpoch >= 0);
