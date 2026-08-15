-- Every Session has to own a counter row, and the only way to guarantee that is to create it inside
-- the same transaction that creates the Session. A Session created without one would fail its first
-- claim at the quota compare-and-swap rather than at admission, and a caller could otherwise reach
-- an unmetered turn simply by using a creation path that forgot the counters. The row is exactly
-- zeroed, which is the one insert the quota trigger accepts without a capacity authorization: it
-- creates an owner and grants no capacity.
CREATE TRIGGER IF NOT EXISTS Sessions_turn_quota_state
AFTER INSERT ON "Sessions"
BEGIN
    INSERT INTO session_turn_quota_state (
        SessionId,
        ClaimCount,
        ReservedFinalizationCount,
        ConsumedFinalizationCount)
    VALUES (NEW."Id", 0, 0, 0);
END;
