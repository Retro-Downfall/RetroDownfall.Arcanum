-- A resolution receipt is the answer already returned for one operation ID and request digest. A
-- replay is answered from this row, so rewriting it would let the second attempt receive a different
-- result than the first, and would silently restate which binding the one-time transition produced.
-- There is no second resolution to record: the Session is final after this row exists.
CREATE TRIGGER IF NOT EXISTS session_campaign_binding_resolution_receipts_guard_update
BEFORE UPDATE ON session_campaign_binding_resolution_receipts
BEGIN
    SELECT RAISE(ABORT, 'session_campaign_binding_resolution_receipts is append-only; existing rows cannot be updated.');
END;
