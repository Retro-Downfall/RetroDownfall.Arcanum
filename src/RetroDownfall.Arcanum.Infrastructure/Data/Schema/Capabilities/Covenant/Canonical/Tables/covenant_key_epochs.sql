-- IncarnationEpoch is the binding epoch: the value a curation head or version records to name which
-- incarnation of this key it curates. It is fixed when the row is created, 0 for every row created
-- from version 6 on, and a key with no row reads as 0 too, so a pin recorded before a key's first head
-- stays bound when that head creates the row. KeyEpoch keeps moving on every head change and stays the
-- dependency epoch validators compare. Rows that existed before version 6 took their KeyEpoch at the
-- upgrade, which is exactly the epoch their live curation was recorded against.
--
-- NOTE: the column is appended where SQLite's ALTER TABLE ... ADD COLUMN splices it into the stored
-- declaration, so a fresh installation and one the version-6 step evolved store the same text.
CREATE TABLE IF NOT EXISTS covenant_key_epochs (
    -- Keyed by normalized key alone, across every Global and Campaign lane. Rows are therefore
    -- proportional to retained canonical keys rather than to historical Campaign churn, which keeps
    -- Global effect validation O(1) without an unbounded tombstone table or a scan under the write
    -- lock.
    NormalizedKey TEXT NOT NULL PRIMARY KEY CHECK (length(NormalizedKey) BETWEEN 1 AND 128),
    KeyEpoch INTEGER NOT NULL CHECK (KeyEpoch > 0),
    UpdatedAtUtc TEXT NOT NULL
, IncarnationEpoch INTEGER NOT NULL DEFAULT 0 CHECK (IncarnationEpoch >= 0));
