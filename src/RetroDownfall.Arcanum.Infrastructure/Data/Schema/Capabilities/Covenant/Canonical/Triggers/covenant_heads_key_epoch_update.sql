-- Advancing a head, retiring it, and reactivating it are all changes to what that normalized key
-- resolves to, so each one moves the key's dependency epoch exactly as an insert does. A validator
-- holding an earlier epoch must fail its comparison rather than trust a resolution taken before the
-- head moved. The insert branch covers the case where the epoch row was reclaimed while heads for
-- the key still exist.
CREATE TRIGGER IF NOT EXISTS covenant_heads_key_epoch_update
AFTER UPDATE ON covenant_heads
BEGIN
    INSERT INTO covenant_key_epochs(NormalizedKey, KeyEpoch, UpdatedAtUtc)
    VALUES (NEW.NormalizedKey, 1, NEW.UpdatedAtUtc)
    ON CONFLICT(NormalizedKey) DO UPDATE SET
        KeyEpoch = KeyEpoch + 1,
        UpdatedAtUtc = excluded.UpdatedAtUtc;
END;
