CREATE TRIGGER IF NOT EXISTS guild_content_erasure_receipts_guard_update
BEFORE UPDATE ON guild_content_erasure_receipts
BEGIN
    SELECT RAISE(ABORT, 'Guild content and evidence revisions are immutable.');
END;
