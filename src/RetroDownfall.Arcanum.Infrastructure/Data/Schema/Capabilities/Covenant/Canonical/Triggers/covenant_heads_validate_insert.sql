-- A head denormalizes scope, Campaign, normalized key, byte cost, and origin so that resolution and
-- quota reads answer from one row instead of joining the entry and version tables under the write
-- lock. The composite foreign key proves the head points at a version of the right entry and lane;
-- it cannot prove the copied values were transcribed from that version and entry. These checks do,
-- one rule at a time, so a failure names the field that drifted. The row ID check is here for the
-- same reason: allocating a projection row ID must advance covenant_state.NextSearchRowId first, and
-- that counter never moves backward, so a head at or past the counter was never allocated.
CREATE TRIGGER IF NOT EXISTS covenant_heads_validate_insert
BEFORE INSERT ON covenant_heads
BEGIN
    SELECT RAISE(ABORT, 'A covenant head must carry the scope of its entry.')
    WHERE NEW.ScopeCode <> (
        SELECT ScopeCode FROM covenant_entries WHERE EntryId = NEW.EntryId
    );

    SELECT RAISE(ABORT, 'A covenant head must carry the Campaign of its entry.')
    WHERE NEW.CampaignId IS NOT (
        SELECT CampaignId FROM covenant_entries WHERE EntryId = NEW.EntryId
    );

    SELECT RAISE(ABORT, 'A covenant head must carry the normalized key of its entry.')
    WHERE NEW.NormalizedKey <> (
        SELECT NormalizedKey FROM covenant_entries WHERE EntryId = NEW.EntryId
    );

    SELECT RAISE(ABORT, 'A covenant head must carry the compiled byte cost of its current version.')
    WHERE NEW.CompiledByteCost <> (
        SELECT CompiledByteCost FROM covenant_versions WHERE VersionId = NEW.CurrentVersionId
    );

    SELECT RAISE(ABORT, 'A covenant head must carry the origin of its current version.')
    WHERE NEW.OriginCode <> (
        SELECT OriginCode FROM covenant_versions WHERE VersionId = NEW.CurrentVersionId
    );

    SELECT RAISE(ABORT, 'A covenant head search row ID must already be allocated by covenant_state.')
    WHERE NEW.SearchRowId >= (
        SELECT NextSearchRowId FROM covenant_state WHERE StateKey = 1
    );
END;
