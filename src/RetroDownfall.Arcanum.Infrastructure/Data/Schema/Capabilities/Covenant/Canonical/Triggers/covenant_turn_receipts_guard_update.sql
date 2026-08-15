-- A turn receipt closes one turn: its chain heads, token costs, and outcome are folded in order into
-- covenant_turn_receipt_aggregate, whose digest is evidence that this exact sequence folded. Editing
-- a receipt after the fact would silently disagree with an aggregate that has already counted it.
CREATE TRIGGER IF NOT EXISTS covenant_turn_receipts_guard_update
BEFORE UPDATE ON covenant_turn_receipts
BEGIN
    SELECT RAISE(ABORT, 'covenant_turn_receipts is append-only; existing rows cannot be updated.');
END;
