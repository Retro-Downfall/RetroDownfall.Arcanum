-- A finalization guard is the durable proof that one assistant placeholder reached a terminal
-- outcome, and every guard consumes one slot of the Session's guard capacity. The reservation has to
-- already be Consumed for this exact assistant identity in this exact Session before the guard can
-- be written, otherwise a retry that lost its reservation would create a guard nobody counted and
-- the ceiling would stop describing the rows that exist.
--
-- An erased assistant entry cannot be finalized again. Its content and label are gone and its
-- receipt is the terminal answer, so a second guard could only be an attempt to resurrect the
-- response the erasure removed. Note that empty committed content is perfectly valid and is checked
-- for nothing here: a successful empty answer is an ordinary guard, not a missing one.
CREATE TRIGGER IF NOT EXISTS assistant_entry_finalizations_validate_insert
BEFORE INSERT ON assistant_entry_finalizations
BEGIN
    SELECT RAISE(ABORT, 'An assistant finalization requires a consumed capacity reservation for the same Session and assistant identity.')
    WHERE NOT EXISTS (
        SELECT 1
        FROM assistant_finalization_capacity_reservations
        WHERE AssistantEntryId = NEW.AssistantEntryId
            AND SessionId = NEW.SessionId
            AND StateCode = 2
    );

    SELECT RAISE(ABORT, 'An erased assistant entry cannot receive a finalization guard.')
    WHERE EXISTS (
        SELECT 1
        FROM assistant_entry_erasure_receipts
        WHERE AssistantEntryId = NEW.AssistantEntryId
    );
END;
