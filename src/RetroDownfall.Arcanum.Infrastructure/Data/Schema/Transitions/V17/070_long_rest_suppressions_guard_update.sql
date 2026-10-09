CREATE TRIGGER IF NOT EXISTS long_rest_suppressions_guard_update
BEFORE UPDATE ON long_rest_suppressions
BEGIN
    SELECT RAISE(ABORT, 'long_rest_suppressions is append-only; transformations are immutable.');
END;
