-- A binding leaves only with its Session, through the retention cascade, or with its owner, through
-- cleanup. Both authorizations begin FALSE on every connection, so an unscoped delete reaches this
-- guard and aborts rather than leaving a Session whose missing binding would be read as an integrity
-- failure. Campaign deletion in particular may clear its own rows but may not remove this one.
CREATE TRIGGER IF NOT EXISTS session_campaign_bindings_guard_delete
BEFORE DELETE ON session_campaign_bindings
WHEN arcanum_session_retention_authorized() = 0 AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'A Session Campaign binding delete requires an authorized retention or cleanup scope.');
END;
