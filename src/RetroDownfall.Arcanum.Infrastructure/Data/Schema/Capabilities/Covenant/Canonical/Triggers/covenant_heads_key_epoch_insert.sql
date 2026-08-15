-- Global effect validation compares a recorded epoch for a normalized key instead of rescanning
-- every Campaign head that shares it, so the epoch has to move whenever a head for that key appears.
-- The counter is keyed by normalized key alone, so one bump covers every scope and lane using it,
-- and the first head to claim a key starts the counter at one.
CREATE TRIGGER IF NOT EXISTS covenant_heads_key_epoch_insert
AFTER INSERT ON covenant_heads
BEGIN
    INSERT INTO covenant_key_epochs(NormalizedKey, KeyEpoch, UpdatedAtUtc)
    VALUES (NEW.NormalizedKey, 1, NEW.UpdatedAtUtc)
    ON CONFLICT(NormalizedKey) DO UPDATE SET
        KeyEpoch = KeyEpoch + 1,
        UpdatedAtUtc = excluded.UpdatedAtUtc;
END;
