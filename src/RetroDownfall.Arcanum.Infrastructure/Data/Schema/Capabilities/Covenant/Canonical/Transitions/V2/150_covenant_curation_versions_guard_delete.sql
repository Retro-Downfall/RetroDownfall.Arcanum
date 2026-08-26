-- Curation versions are removed only by owner cleanup or Covenant family maintenance, which take the
-- whole chain and everything pointing at it in one transaction. Deleting one version outside that
-- scope would break the predecessor chain and orphan the head that still names it. Both
-- authorizations begin FALSE on every connection, so ordinary application work reaches this guard.
CREATE TRIGGER IF NOT EXISTS covenant_curation_versions_guard_delete
BEFORE DELETE ON covenant_curation_versions
WHEN arcanum_owner_cleanup_authorized() = 0 AND arcanum_covenant_family_maintenance_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_curation_versions delete requires an authorized cleanup scope.');
END;
