-- Deletion changes the Campaign set exactly as much as insertion does, so it advances the same
-- epoch. Skipping it here would let a reader that cached its view before the delete keep answering
-- from a Campaign that no longer exists. The maximum is refused before the increment rather than
-- after, because a signed 64-bit wrap would reissue an epoch the installation has already used.
CREATE TRIGGER IF NOT EXISTS campaign_registry_state_campaign_delete
AFTER DELETE ON "Campaigns"
BEGIN
    SELECT RAISE(ABORT, 'The Campaign registry epoch has reached its maximum and cannot advance.')
    WHERE (SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1) = 9223372036854775807;

    UPDATE campaign_registry_state
    SET RegistryEpoch = RegistryEpoch + 1
    WHERE StateKey = 1;
END;
