CREATE TRIGGER IF NOT EXISTS artifact_sensitivity_clock_guard_delete
BEFORE DELETE ON artifact_sensitivity_clock
BEGIN
    SELECT RAISE(ABORT, 'The sensitivity clock cannot be rewound.');
END;
