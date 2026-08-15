-- The requested identity and apply-request digest are what make a replayed request resolve to the
-- operation it already created. Rewriting either would let a second, different request adopt an
-- existing operation's result, so the row is frozen at insert and there is no correcting edit.
CREATE TRIGGER IF NOT EXISTS long_running_operation_request_identities_guard_update
BEFORE UPDATE ON long_running_operation_request_identities
BEGIN
    SELECT RAISE(ABORT, 'A long-running operation request identity is immutable and can never be updated.');
END;
