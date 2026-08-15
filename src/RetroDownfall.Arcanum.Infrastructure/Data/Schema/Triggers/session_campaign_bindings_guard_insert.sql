-- A binding is written only by the transaction that creates the Session or by the core data
-- initializer that backfills a legacy one. Both borrow the Session binding write scope on their own
-- live connection, and that scope begins FALSE on every connection, so ordinary application code and
-- direct SQL reach this guard and abort. Without it, any writer could fabricate Campaign authority
-- for a Session simply by inserting a row.
CREATE TRIGGER IF NOT EXISTS session_campaign_bindings_guard_insert
BEFORE INSERT ON session_campaign_bindings
WHEN arcanum_session_binding_write_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'A Session Campaign binding insert requires the Session binding write scope.');
END;
