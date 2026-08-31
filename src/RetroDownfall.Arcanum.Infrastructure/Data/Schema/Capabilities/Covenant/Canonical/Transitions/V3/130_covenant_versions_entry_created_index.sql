CREATE INDEX IF NOT EXISTS idx_covenant_versions_entry_created
    ON covenant_versions(EntryId, CreatedAtUtc);
