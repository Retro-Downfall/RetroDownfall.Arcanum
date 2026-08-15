-- Removing a head changes that key's resolution as much as adding one does, and Campaign cleanup
-- removes heads in bulk, which is exactly when a validator is most likely to be holding a stale
-- resolution. The epoch row may already have been reclaimed for this key, so the same upsert shape
-- is used rather than a bare update, keyed on the removed row's key and timestamp.
CREATE TRIGGER IF NOT EXISTS covenant_heads_key_epoch_delete
AFTER DELETE ON covenant_heads
BEGIN
    INSERT INTO covenant_key_epochs(NormalizedKey, KeyEpoch, UpdatedAtUtc)
    VALUES (OLD.NormalizedKey, 1, OLD.UpdatedAtUtc)
    ON CONFLICT(NormalizedKey) DO UPDATE SET
        KeyEpoch = KeyEpoch + 1,
        UpdatedAtUtc = excluded.UpdatedAtUtc;
END;
