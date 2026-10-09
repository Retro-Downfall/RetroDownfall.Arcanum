CREATE TRIGGER IF NOT EXISTS campaign_rollup_sources_guard_update
BEFORE UPDATE ON campaign_rollup_sources
BEGIN
    SELECT RAISE(ABORT, 'campaign_rollup_sources revisions are immutable.');
END;
