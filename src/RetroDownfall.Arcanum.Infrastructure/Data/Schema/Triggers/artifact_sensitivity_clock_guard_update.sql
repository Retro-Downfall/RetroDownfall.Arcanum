CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_clock_guard_update
BEFORE UPDATE ON artifact_sensitivity_clock
WHEN NEW.Id <> OLD.Id OR OLD.Revision = 9223372036854775807 OR NEW.Revision <> OLD.Revision + 1
BEGIN
    SELECT RAISE(ABORT, 'The sensitivity clock only advances by one committed mutation.');
END;
