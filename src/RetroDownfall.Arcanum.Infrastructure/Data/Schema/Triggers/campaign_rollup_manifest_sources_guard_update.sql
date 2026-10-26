CREATE TRIGGER IF NOT EXISTS campaign_rollup_manifest_sources_guard_update
BEFORE UPDATE ON campaign_rollup_manifest_sources
BEGIN
    SELECT RAISE(ABORT, 'Campaign provenance segments are immutable.');
END;
