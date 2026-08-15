-- A tombstone is audit evidence that survives the rows it replaced, so it is removed only by a later
-- authenticated full-installation reset or ordinary evidence-retention policy. Removing it restores
-- nothing: the stripped authority is gone and this row never held enough to rebuild it. The staging
-- authorization is refused so a restore cannot erase the record of its own sanitation.
CREATE TRIGGER IF NOT EXISTS restored_managed_file_authority_tombstones_guard_delete
BEFORE DELETE ON restored_managed_file_authority_tombstones
BEGIN
    SELECT RAISE(ABORT, 'A restore-staging authority tombstone delete requires owner-cleanup or family-maintenance authorization.')
    WHERE arcanum_owner_cleanup_authorized() = 0
        AND arcanum_covenant_family_maintenance_authorized() = 0;

    SELECT RAISE(ABORT, 'A restore-staging authority tombstone cannot be deleted under the staging authorization that wrote it.')
    WHERE arcanum_restore_staging_managed_authority_sanitization_authorized() = 1;
END;
