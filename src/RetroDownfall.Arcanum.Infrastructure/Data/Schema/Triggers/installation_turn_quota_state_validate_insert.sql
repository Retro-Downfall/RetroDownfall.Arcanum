-- The singleton is seeded by the core initializer, which holds no capacity authorization, so the
-- same narrow structural exception applies: an exactly zeroed row may be created because it grants
-- nothing, and the fixed primary key refuses a second one. A seed carrying nonzero counters would be
-- installation-wide capacity conjured at startup, so it needs the same authorized turn capacity
-- scope as every later counter move.
CREATE TRIGGER IF NOT EXISTS installation_turn_quota_state_validate_insert
BEFORE INSERT ON installation_turn_quota_state
BEGIN
    SELECT RAISE(ABORT, 'A nonzero installation turn quota row requires an authorized turn capacity mutation scope.')
    WHERE arcanum_turn_capacity_mutation_authorized() = 0
        AND (NEW.ClaimCount <> 0
            OR NEW.ReservedFinalizationCount <> 0
            OR NEW.ConsumedFinalizationCount <> 0);
END;
