-- A deleted source row leaves no text for a stored hash to describe.
CREATE TRIGGER IF NOT EXISTS Entries_drop_tapestry_leaf_hash_delete
AFTER DELETE ON Entries
BEGIN
    DELETE FROM tapestry_leaf_hashes
    WHERE SourceKind = 'Entry' AND SourceId = OLD.Id;
END;
