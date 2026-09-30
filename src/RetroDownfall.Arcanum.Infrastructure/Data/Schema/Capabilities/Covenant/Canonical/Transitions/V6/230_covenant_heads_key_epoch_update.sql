-- Replaces the version-5 trigger with the head text, which names the binding epoch of a key row it
-- creates. A trigger is a whole object and the drift gate compares its installed text against the
-- head tree, so the older text cannot stay beside the new one.
DROP TRIGGER IF EXISTS covenant_heads_key_epoch_update;

CREATE TRIGGER IF NOT EXISTS covenant_heads_key_epoch_update
AFTER UPDATE ON covenant_heads
BEGIN
    INSERT INTO covenant_key_epochs(NormalizedKey, KeyEpoch, UpdatedAtUtc, IncarnationEpoch)
    VALUES (NEW.NormalizedKey, 1, NEW.UpdatedAtUtc, 0)
    ON CONFLICT(NormalizedKey) DO UPDATE SET
        KeyEpoch = KeyEpoch + 1,
        UpdatedAtUtc = excluded.UpdatedAtUtc;
END;
