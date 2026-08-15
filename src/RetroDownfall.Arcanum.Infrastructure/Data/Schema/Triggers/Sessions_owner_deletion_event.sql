-- Session deletion is failure-isolated in the same way Campaign deletion is: the core transaction
-- appends its owner event and commits, and each capability consumes the event later at its own pace.
--
-- Both optional owner fields are always null. There is no closed Session-deletion protocol that
-- prepares an exclusive intent today, and fabricating an operation ID and effect digest here would
-- make an unmanaged deletion indistinguishable from a managed one in the permanent journal.
CREATE TRIGGER IF NOT EXISTS Sessions_owner_deletion_event
AFTER DELETE ON "Sessions"
BEGIN
    INSERT INTO owner_deletion_events (
        OwnerKindCode,
        OwnerId,
        OperationId,
        ExclusiveEffectDigest,
        DeletedAtUtc)
    VALUES (
        2,
        OLD."Id",
        NULL,
        NULL,
        strftime('%Y-%m-%dT%H:%M:%f', 'now') || '0000Z');
END;
