-- The aggregate is the folded head of a Session's turn receipt tail, and folding only ever adds
-- coverage: rows leave the tail and their costs and outcomes land here. A total that shrinks, or an
-- aggregate that changes Session, means a fold was lost or replayed against the wrong tail, and the
-- chain digest would then attest to a sequence that never happened. The earliest covered timestamp
-- is fixed once the aggregate is nonempty for the same reason: folding extends the range forward,
-- never backward.
CREATE TRIGGER IF NOT EXISTS covenant_turn_receipt_aggregate_validate_update
BEFORE UPDATE ON covenant_turn_receipt_aggregate
BEGIN
    SELECT RAISE(ABORT, 'covenant_turn_receipt_aggregate cannot change the session it folds.')
    WHERE NEW.SessionId <> OLD.SessionId;

    SELECT RAISE(ABORT, 'covenant_turn_receipt_aggregate covered count cannot decrease.')
    WHERE NEW.CoveredCount < OLD.CoveredCount;

    SELECT RAISE(ABORT, 'covenant_turn_receipt_aggregate confirmed token total cannot decrease.')
    WHERE NEW.ConfirmedTokenTotal < OLD.ConfirmedTokenTotal;

    SELECT RAISE(ABORT, 'covenant_turn_receipt_aggregate proposed token total cannot decrease.')
    WHERE NEW.ProposedTokenTotal < OLD.ProposedTokenTotal;

    SELECT RAISE(ABORT, 'covenant_turn_receipt_aggregate mutation total cannot decrease.')
    WHERE NEW.MutationTotal < OLD.MutationTotal;

    SELECT RAISE(ABORT, 'covenant_turn_receipt_aggregate completed outcome count cannot decrease.')
    WHERE NEW.CompletedOutcomeCount < OLD.CompletedOutcomeCount;

    SELECT RAISE(ABORT, 'covenant_turn_receipt_aggregate failed outcome count cannot decrease.')
    WHERE NEW.FailedOutcomeCount < OLD.FailedOutcomeCount;

    SELECT RAISE(ABORT, 'covenant_turn_receipt_aggregate cancelled outcome count cannot decrease.')
    WHERE NEW.CancelledOutcomeCount < OLD.CancelledOutcomeCount;

    SELECT RAISE(ABORT, 'covenant_turn_receipt_aggregate interrupted outcome count cannot decrease.')
    WHERE NEW.InterruptedOutcomeCount < OLD.InterruptedOutcomeCount;

    -- IS NOT rather than <>, so a rewrite that drops the timestamp to null is caught here too.
    SELECT RAISE(ABORT, 'covenant_turn_receipt_aggregate earliest covered timestamp is fixed once it is nonempty.')
    WHERE OLD.CoveredCount > 0
        AND NEW.EarliestCoveredAtUtc IS NOT OLD.EarliestCoveredAtUtc;
END;
