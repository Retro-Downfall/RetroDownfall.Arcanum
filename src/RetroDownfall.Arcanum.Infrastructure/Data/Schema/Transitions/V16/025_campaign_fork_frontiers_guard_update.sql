CREATE TRIGGER IF NOT EXISTS campaign_fork_frontiers_guard_update
BEFORE UPDATE ON campaign_fork_frontiers
BEGIN
    SELECT RAISE(ABORT, 'campaign_fork_frontiers revisions are immutable.');
END;
