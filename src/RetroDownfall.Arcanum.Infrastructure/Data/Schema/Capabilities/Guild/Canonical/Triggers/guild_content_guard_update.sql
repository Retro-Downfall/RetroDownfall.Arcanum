CREATE TRIGGER IF NOT EXISTS guild_content_guard_update
BEFORE UPDATE ON guild_content
BEGIN
    SELECT RAISE(ABORT, 'Guild content and evidence revisions are immutable.');
END;
