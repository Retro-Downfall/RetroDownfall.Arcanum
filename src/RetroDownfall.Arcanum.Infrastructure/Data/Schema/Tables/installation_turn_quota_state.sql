-- The installation-wide half of the turn capacity ledger, a singleton. Per-Session ceilings alone
-- would let an unbounded number of Sessions each sit just under their limit, so the same two
-- resources are counted again across the whole installation. Whole-Session retention is the only
-- path that reduces these counters, and it decrements them by the exact values locked from the
-- Session row it is removing.
CREATE TABLE IF NOT EXISTS installation_turn_quota_state (
    StateKey INTEGER NOT NULL PRIMARY KEY CHECK (StateKey = 1),
    ClaimCount INTEGER NOT NULL CHECK (ClaimCount BETWEEN 0 AND 1048576),
    ReservedFinalizationCount INTEGER NOT NULL CHECK (ReservedFinalizationCount >= 0),
    ConsumedFinalizationCount INTEGER NOT NULL CHECK (ConsumedFinalizationCount >= 0),
    CHECK (ReservedFinalizationCount + ConsumedFinalizationCount <= 1048576)
);
