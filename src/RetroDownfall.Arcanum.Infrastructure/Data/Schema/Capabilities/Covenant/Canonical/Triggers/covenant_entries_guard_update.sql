-- An entry is the durable identity every version, head, and receipt was recorded against. Rewriting
-- its scope, key, or creation time would retroactively change what those rows refer to, so the entry
-- row is fixed at insert and later facts arrive as new versions instead.
CREATE TRIGGER IF NOT EXISTS covenant_entries_guard_update
BEFORE UPDATE ON covenant_entries
BEGIN
    SELECT RAISE(ABORT, 'covenant_entries is append-only; existing rows cannot be updated.');
END;
