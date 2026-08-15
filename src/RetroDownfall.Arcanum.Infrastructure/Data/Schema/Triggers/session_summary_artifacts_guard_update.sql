-- The artifact exists to give a mutable summary an identity that cannot change under its label. If
-- the row could be edited, the content digest could be moved to match new bytes and a label issued
-- for the old summary would authorize the new one. A replacement inserts the next revision instead.
CREATE TRIGGER IF NOT EXISTS session_summary_artifacts_guard_update
BEFORE UPDATE ON session_summary_artifacts
BEGIN
    SELECT RAISE(ABORT, 'session_summary_artifacts rows are immutable; a replacement is the next revision.');
END;
