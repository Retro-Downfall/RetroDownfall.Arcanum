CREATE TRIGGER IF NOT EXISTS long_rest_discovery_leaves_guard_update
BEFORE UPDATE ON long_rest_discovery_leaves
BEGIN
    SELECT RAISE(ABORT, 'Long Rest accepted evidence is immutable.');
END;
