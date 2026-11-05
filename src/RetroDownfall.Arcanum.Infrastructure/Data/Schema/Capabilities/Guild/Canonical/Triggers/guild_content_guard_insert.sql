CREATE TRIGGER IF NOT EXISTS guild_content_guard_insert
BEFORE INSERT ON guild_content
WHEN EXISTS(SELECT 1 FROM guild_content_erasure_receipts WHERE ContentId=NEW.ContentId)
    OR EXISTS(SELECT 1 FROM guild_content_erasure_receipts
        WHERE ContentKindCode=NEW.ContentKindCode AND SourceRecordId=NEW.SourceRecordId AND Revision=NEW.Revision)
BEGIN
    SELECT RAISE(ABORT, 'An erased Guild content identity or immutable revision cannot be resurrected.');
END;
