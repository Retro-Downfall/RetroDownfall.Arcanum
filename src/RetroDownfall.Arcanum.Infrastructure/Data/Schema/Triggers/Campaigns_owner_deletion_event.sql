-- Every capability that owns Campaign-scoped data needs an ordered record that the Campaign is gone,
-- and it must get one whether or not that capability is installed. Putting the append here, on core
-- Campaign deletion, is what makes optional tiers failure-isolated: deletion commits even when every
-- Covenant object has been dropped.
--
-- A managed deletion prepared its intent before deleting the Campaign, so the event copies that
-- exact operation and effect digest and advances the intent to OwnerDeleted in the same transaction.
-- An unmanaged deletion finds no intent and leaves both optional fields null. The subselects are
-- deliberately allowed to return nothing: a trigger that invented an effect digest would attribute a
-- permanent journal entry to an operation that never committed to it.
CREATE TRIGGER IF NOT EXISTS Campaigns_owner_deletion_event
AFTER DELETE ON "Campaigns"
BEGIN
    INSERT INTO owner_deletion_events (
        OwnerKindCode,
        OwnerId,
        OperationId,
        ExclusiveEffectDigest,
        DeletedAtUtc)
    VALUES (
        1,
        OLD."Id",
        (
            SELECT OperationId
            FROM owner_deletion_operation_intents
            WHERE OwnerKindCode = 1
                AND OwnerId = OLD."Id"
                AND PhaseCode = 1
        ),
        (
            SELECT ExclusiveEffectDigest
            FROM owner_deletion_operation_intents
            WHERE OwnerKindCode = 1
                AND OwnerId = OLD."Id"
                AND PhaseCode = 1
        ),
        strftime('%Y-%m-%dT%H:%M:%f', 'now') || '0000Z');

    UPDATE owner_deletion_operation_intents
    SET PhaseCode = 2,
        Revision = Revision + 1,
        UpdatedAtUtc = strftime('%Y-%m-%dT%H:%M:%f', 'now') || '0000Z'
    WHERE OwnerKindCode = 1
        AND OwnerId = OLD."Id"
        AND PhaseCode = 1;
END;
