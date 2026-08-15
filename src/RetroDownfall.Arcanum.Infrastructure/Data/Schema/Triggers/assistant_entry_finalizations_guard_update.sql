-- The guard is one-shot. A retry resolves through the stored terminal outcome, so an editable row
-- would let a second attempt rewrite the answer the first one already returned: a Discarded turn
-- could become Committed, or a Committed one could be pointed at different content. A correction is
-- not representable here, and that is the point.
CREATE TRIGGER IF NOT EXISTS assistant_entry_finalizations_guard_update
BEFORE UPDATE ON assistant_entry_finalizations
BEGIN
    SELECT RAISE(ABORT, 'assistant_entry_finalizations rows are terminal and cannot be updated.');
END;
