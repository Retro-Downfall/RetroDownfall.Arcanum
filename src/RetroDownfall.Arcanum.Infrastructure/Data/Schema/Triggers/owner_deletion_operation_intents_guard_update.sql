-- The phase of this row is what a crashed deletion resumes from, so every advance has to be both
-- authorized and unambiguous. The compare-and-swap on Revision is the ambiguity fix: two coordinators
-- that both read Prepared cannot both write OwnerDeleted, because the second one no longer holds the
-- revision it read. The phase graph is strictly monotonic by one, so a skipped phase can never claim
-- evidence a step never produced, and a terminal row can never be reopened.
--
-- The trigger-driven Prepared to OwnerDeleted advance inside Campaign deletion passes through here
-- too. It is authorized because the deleting transaction holds the same scope, not because a trigger
-- is exempt.
CREATE TRIGGER IF NOT EXISTS owner_deletion_operation_intents_guard_update
BEFORE UPDATE ON owner_deletion_operation_intents
BEGIN
    SELECT RAISE(ABORT, 'An owner-deletion intent update requires owner-cleanup authorization.')
    WHERE arcanum_owner_cleanup_authorized() = 0;

    SELECT RAISE(ABORT, 'An owner-deletion intent identity, owner, operation code, and effect digest are immutable.')
    WHERE NEW.OperationId <> OLD.OperationId
        OR NEW.OwnerKindCode <> OLD.OwnerKindCode
        OR NEW.OwnerId <> OLD.OwnerId
        OR NEW.OperationCode <> OLD.OperationCode
        OR NEW.ExclusiveEffectDigest <> OLD.ExclusiveEffectDigest
        OR NEW.CreatedAtUtc <> OLD.CreatedAtUtc;

    SELECT RAISE(ABORT, 'An owner-deletion intent update requires the exact prior revision.')
    WHERE NEW.Revision <> OLD.Revision + 1;

    SELECT RAISE(ABORT, 'An owner-deletion intent advances exactly one phase and never regresses.')
    WHERE NEW.PhaseCode <> OLD.PhaseCode + 1;
END;
