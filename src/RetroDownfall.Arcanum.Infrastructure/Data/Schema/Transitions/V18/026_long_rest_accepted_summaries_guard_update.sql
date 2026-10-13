CREATE TRIGGER IF NOT EXISTS long_rest_accepted_summaries_guard_update
BEFORE UPDATE ON long_rest_accepted_summaries
BEGIN
    SELECT RAISE(ABORT, 'Long Rest accepted evidence is immutable.');
END;
