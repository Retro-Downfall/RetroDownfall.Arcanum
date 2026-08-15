-- Global Covenant preflight decides whether its cached Campaign view is current by comparing this
-- epoch for equality, so the epoch has to move in the same transaction that adds the Campaign.
-- Advancing it from a trigger rather than from application code is what makes that true for every
-- writer, including EF, direct SQL, and restore reconciliation. The maximum is refused before the
-- increment rather than after, because a signed 64-bit wrap would hand back an epoch the
-- installation has already used and a stale reader would compare equal to it.
CREATE TRIGGER IF NOT EXISTS campaign_registry_state_campaign_insert
AFTER INSERT ON "Campaigns"
BEGIN
    SELECT RAISE(ABORT, 'The Campaign registry epoch has reached its maximum and cannot advance.')
    WHERE (SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1) = 9223372036854775807;

    UPDATE campaign_registry_state
    SET RegistryEpoch = RegistryEpoch + 1
    WHERE StateKey = 1;
END;
