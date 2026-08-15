-- Creating a counter owner and granting capacity are different acts. The core initializer and the
-- Sessions parent-creation trigger both need to create the owner row for a Session that has none,
-- and neither of them holds a capacity authorization, so an exactly zeroed insert is allowed
-- structurally. It is the narrowest possible exception: it can only produce a row that has reserved,
-- consumed, and claimed nothing, and the primary key already refuses a second one. Any insert
-- carrying a nonzero counter is capacity being created out of nothing and requires the same
-- authorized turn capacity scope every other counter move does.
CREATE TRIGGER IF NOT EXISTS session_turn_quota_state_validate_insert
BEFORE INSERT ON session_turn_quota_state
BEGIN
    SELECT RAISE(ABORT, 'A nonzero session turn quota row requires an authorized turn capacity mutation scope.')
    WHERE arcanum_turn_capacity_mutation_authorized() = 0
        AND (NEW.ClaimCount <> 0
            OR NEW.ReservedFinalizationCount <> 0
            OR NEW.ConsumedFinalizationCount <> 0);
END;
