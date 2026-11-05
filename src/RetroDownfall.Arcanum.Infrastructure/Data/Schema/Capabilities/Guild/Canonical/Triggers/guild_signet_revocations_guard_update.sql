CREATE TRIGGER IF NOT EXISTS guild_signet_revocations_guard_update
BEFORE UPDATE ON guild_signet_revocations
BEGIN
    SELECT RAISE(ABORT, 'Guild revocation receipts are immutable.');
END;
