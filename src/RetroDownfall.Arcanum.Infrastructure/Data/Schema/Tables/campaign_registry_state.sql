CREATE TABLE IF NOT EXISTS campaign_registry_state (
    -- Fixed to 1: this table is a singleton, and a key that can only hold one value is the cheapest
    -- way to say so in SQLite without a trigger.
    StateKey INTEGER NOT NULL PRIMARY KEY CHECK (StateKey = 1),
    -- Global Covenant preflight compares this epoch for equality to decide whether its cached
    -- Campaign view is still current. It lives in an always-present core row precisely so that
    -- optional Covenant damage cannot make ordinary Campaign CRUD depend on Covenant availability.
    -- Zero is excluded so an unseeded row can never compare equal to a real observation.
    RegistryEpoch INTEGER NOT NULL CHECK (RegistryEpoch > 0)
);
