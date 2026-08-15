-- The epoch is only useful because it is monotonic: a validator proves nothing changed by comparing
-- a recorded value for equality. An epoch that repeated or moved backward would let a stale
-- recording compare equal to a key that has since changed. The maximum is refused before the
-- increment rather than after, because a signed 64-bit wrap would hand back a value the key has
-- already used. A key that reaches the maximum fails closed and needs a reclamation, not a wrap.
CREATE TRIGGER IF NOT EXISTS covenant_key_epochs_guard_overflow
BEFORE UPDATE ON covenant_key_epochs
BEGIN
    SELECT RAISE(ABORT, 'A covenant key epoch can only advance.')
    WHERE NEW.KeyEpoch <= OLD.KeyEpoch;

    SELECT RAISE(ABORT, 'A covenant key epoch has reached its maximum and cannot advance.')
    WHERE OLD.KeyEpoch = 9223372036854775807;
END;
