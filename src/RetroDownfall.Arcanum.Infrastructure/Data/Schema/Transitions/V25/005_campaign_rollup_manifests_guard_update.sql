CREATE TRIGGER IF NOT EXISTS campaign_rollup_manifests_guard_update
BEFORE UPDATE ON campaign_rollup_manifests
BEGIN
    SELECT RAISE(ABORT, 'Campaign provenance segments are immutable.');
END;
