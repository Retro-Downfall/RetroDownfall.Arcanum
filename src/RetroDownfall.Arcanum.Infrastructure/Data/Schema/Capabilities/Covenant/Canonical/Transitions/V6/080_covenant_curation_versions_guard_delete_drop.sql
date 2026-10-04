-- The purge below deletes curation rows outside any authorization scope, which the version-5 guard
-- refuses. It is dropped here and recreated from the head text once the purge is done.
DROP TRIGGER IF EXISTS covenant_curation_versions_guard_delete;
