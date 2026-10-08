-- The row a source row's insert makes new has no stored hash yet. This also covers an INSERT OR REPLACE
-- over an existing source row, which removes the old row without firing the delete trigger.
CREATE TRIGGER IF NOT EXISTS Entries_drop_tapestry_leaf_hash_insert
AFTER INSERT ON Entries
BEGIN
    DELETE FROM tapestry_leaf_hashes
    WHERE SourceKind = 'Entry' AND SourceId = NEW.Id;
END;
