-- Versions are removed only by owner cleanup, Covenant family maintenance, or the erasure of the entry
-- they belong to, which take the whole chain and everything pointing at it in one transaction.
-- Deleting one version outside that scope would break the predecessor chain and orphan the head that
-- still names it. All three authorizations begin FALSE on every connection, so ordinary application
-- work reaches this guard and aborts.
CREATE TRIGGER IF NOT EXISTS covenant_versions_guard_delete
BEFORE DELETE ON covenant_versions
WHEN arcanum_owner_cleanup_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
    AND arcanum_covenant_entry_erasure_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_versions delete requires an authorized cleanup scope.');
END;
