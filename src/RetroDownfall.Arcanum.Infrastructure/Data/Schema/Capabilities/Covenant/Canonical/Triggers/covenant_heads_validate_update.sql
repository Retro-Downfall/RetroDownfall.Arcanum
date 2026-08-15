-- Advancing a head rewrites the denormalized copy, so the same projection rules that admit the row
-- have to hold after every advance, retirement, and reactivation. Three fields are additionally
-- frozen. EntryId and LaneCode are the primary key, and an update that moved them would rename the
-- head instead of advancing it, leaving the row the caller meant to change untouched. SearchRowId is
-- allocated once for that entry and lane and never reused within a dataset generation, so moving it
-- would hand the accelerator row of one head to another.
CREATE TRIGGER IF NOT EXISTS covenant_heads_validate_update
BEFORE UPDATE ON covenant_heads
BEGIN
    SELECT RAISE(ABORT, 'A covenant head cannot change the entry it belongs to.')
    WHERE NEW.EntryId <> OLD.EntryId;

    SELECT RAISE(ABORT, 'A covenant head cannot change its lane.')
    WHERE NEW.LaneCode <> OLD.LaneCode;

    SELECT RAISE(ABORT, 'A covenant head cannot change its search row ID.')
    WHERE NEW.SearchRowId <> OLD.SearchRowId;

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
END;
