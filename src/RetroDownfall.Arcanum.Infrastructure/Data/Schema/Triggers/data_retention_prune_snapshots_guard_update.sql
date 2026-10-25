CREATE TRIGGER IF NOT EXISTS data_retention_prune_snapshots_guard_update
BEFORE UPDATE ON data_retention_prune_snapshots
BEGIN
    SELECT RAISE(ABORT, 'Retention prune snapshots are immutable.');
END;
