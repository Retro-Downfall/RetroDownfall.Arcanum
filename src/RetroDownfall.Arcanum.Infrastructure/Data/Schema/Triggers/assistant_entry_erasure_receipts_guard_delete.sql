-- Removing the receipt without removing the Session would leave a committed assistant entry with
-- neither content nor a tombstone, which is the one integrity state that cannot be told apart from
-- data loss. A retry would then find a finalization guard pointing at nothing instead of receiving
-- Covenant.ArtifactErased. Only Session retention or owner cleanup, which remove the guard and the
-- claim in the same transaction, may take it.
CREATE TRIGGER IF NOT EXISTS assistant_entry_erasure_receipts_guard_delete
BEFORE DELETE ON assistant_entry_erasure_receipts
WHEN arcanum_session_retention_authorized() = 0
    AND arcanum_owner_cleanup_authorized() = 0
BEGIN
    SELECT RAISE(ABORT, 'assistant_entry_erasure_receipts delete requires an authorized retention or owner cleanup scope.');
END;
