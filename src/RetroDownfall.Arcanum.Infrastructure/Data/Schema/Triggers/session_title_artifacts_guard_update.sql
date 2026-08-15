-- A title artifact is immutable for the same reason a summary artifact is: its content digest is
-- what proves a sensitivity label describes these exact bytes. A model-generated title propagates
-- taint, so an editable artifact would let a clean-looking rewrite inherit a tainted label, or a
-- tainted rewrite hide behind a clean one.
CREATE TRIGGER IF NOT EXISTS session_title_artifacts_guard_update
BEFORE UPDATE ON session_title_artifacts
BEGIN
    SELECT RAISE(ABORT, 'session_title_artifacts rows are immutable; a replacement is the next revision.');
END;
