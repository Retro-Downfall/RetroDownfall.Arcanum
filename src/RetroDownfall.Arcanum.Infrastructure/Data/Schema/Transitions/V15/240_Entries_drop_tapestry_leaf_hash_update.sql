-- A stored hash describes the text it was computed from, so changing the text or the id drops it. The
-- WHEN clause leaves an update that rewrites the same value alone.
CREATE TRIGGER IF NOT EXISTS Entries_drop_tapestry_leaf_hash_update
AFTER UPDATE OF Id, Content ON Entries
WHEN NEW.Id IS NOT OLD.Id OR NEW.Content IS NOT OLD.Content
BEGIN
    DELETE FROM tapestry_leaf_hashes
    WHERE SourceKind = 'Entry' AND SourceId IN (OLD.Id, NEW.Id);
END;
