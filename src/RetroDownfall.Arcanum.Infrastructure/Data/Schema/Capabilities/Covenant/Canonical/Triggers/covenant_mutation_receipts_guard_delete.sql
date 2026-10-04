-- Receipts are retained so a repeated request is recognized rather than applied twice, and they also
-- carry the per-scope quota history. They are removed only when their owner is being cleaned up, the
-- Covenant family is being torn down, or the entry they resolved is being erased. All three
-- authorizations begin FALSE on every connection, so ordinary application work reaches this guard and
-- aborts.
CREATE TRIGGER IF NOT EXISTS covenant_mutation_receipts_guard_delete
BEFORE DELETE ON covenant_mutation_receipts
WHEN arcanum_owner_cleanup_authorized() = 0
    AND arcanum_covenant_family_maintenance_authorized() = 0
    AND arcanum_covenant_entry_erasure_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'covenant_mutation_receipts delete requires an authorized cleanup scope.');
END;
