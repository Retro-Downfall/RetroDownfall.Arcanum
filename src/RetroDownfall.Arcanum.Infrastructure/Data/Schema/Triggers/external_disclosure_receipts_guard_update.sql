-- A receipt is an assertion that something physically left this installation. That cannot become
-- untrue later, so the row is frozen the moment it commits. Allowing an update would let a retry
-- rewrite an earlier attempt into itself and make two disclosures look like one.
CREATE TRIGGER IF NOT EXISTS external_disclosure_receipts_guard_update
BEFORE UPDATE ON external_disclosure_receipts
BEGIN
    SELECT RAISE(ABORT, 'external_disclosure_receipts is append-only; a committed receipt cannot be updated.');
END;
