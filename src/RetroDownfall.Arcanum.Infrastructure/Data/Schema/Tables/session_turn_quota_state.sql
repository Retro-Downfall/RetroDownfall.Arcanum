-- The per-Session half of the turn capacity ledger, exactly one row per Session. The counters are
-- lifetime totals, not live gauges: no ordinary terminal claim or guard gives capacity back, so a
-- Session cannot be used as an unbounded append target by retrying forever. The two ceilings are
-- independent because claims and finalization guards are different resources, and a workload that
-- exhausts one must not be able to exhaust the other by proxy.
--
-- Guard capacity is the checked sum of reserved and consumed slots, so consuming a reservation
-- moves a count between the two columns without changing the total the ceiling applies to.
CREATE TABLE IF NOT EXISTS session_turn_quota_state (
    SessionId TEXT NOT NULL PRIMARY KEY REFERENCES "Sessions" ("Id") ON DELETE CASCADE,
    ClaimCount INTEGER NOT NULL CHECK (ClaimCount BETWEEN 0 AND 16384),
    ReservedFinalizationCount INTEGER NOT NULL CHECK (ReservedFinalizationCount >= 0),
    ConsumedFinalizationCount INTEGER NOT NULL CHECK (ConsumedFinalizationCount >= 0),
    CHECK (ReservedFinalizationCount + ConsumedFinalizationCount <= 16384)
);
