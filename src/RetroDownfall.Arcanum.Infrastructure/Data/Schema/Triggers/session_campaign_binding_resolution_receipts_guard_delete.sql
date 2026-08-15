-- The receipt outlives the request it answers, so it leaves only with its owner or under family
-- maintenance. Both authorizations begin FALSE on every connection, so ordinary work reaches this
-- guard and aborts rather than deleting the evidence that a Session's binding was resolved once,
-- deliberately, and by whom.
CREATE TRIGGER IF NOT EXISTS session_campaign_binding_resolution_receipts_guard_delete
BEFORE DELETE ON session_campaign_binding_resolution_receipts
WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'session_campaign_binding_resolution_receipts delete requires an authorized cleanup scope.');
END;
