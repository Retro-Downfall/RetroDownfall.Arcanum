-- A curation head is the operator's standing pin or mask for one subject, so it is removed only with
-- the rest of that subject's curation: by owner cleanup, Covenant family maintenance, or the erasure of
-- the entry whose key it curates. All three authorizations begin FALSE on every connection, so
-- ordinary application work reaches this guard and aborts.
CREATE TRIGGER IF NOT EXISTS covenant_curation_heads_guard_delete
BEFORE DELETE ON covenant_curation_heads
WHEN arcanum_owner_cleanup_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
    AND arcanum_covenant_entry_erasure_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_curation_heads delete requires an authorized cleanup scope.');
END;
