CREATE TRIGGER IF NOT EXISTS long_rest_adjudications_guard_update
BEFORE UPDATE ON long_rest_adjudications
BEGIN
    SELECT RAISE(ABORT, 'Long Rest accepted evidence is immutable.');
END;
