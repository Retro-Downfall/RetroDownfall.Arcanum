CREATE TRIGGER IF NOT EXISTS guild_work_bids_guard_update BEFORE UPDATE ON guild_work_bids
WHEN NEW.Id IS NOT OLD.Id OR NEW.OfferId IS NOT OLD.OfferId OR NEW.MemberId IS NOT OLD.MemberId OR NEW.AgentId IS NOT OLD.AgentId
 OR NEW.AuthorQuestId IS NOT OLD.AuthorQuestId OR NEW.CardRevision IS NOT OLD.CardRevision OR NEW.BidSequence IS NOT OLD.BidSequence OR NEW.NoteContentId IS NOT OLD.NoteContentId
 OR NEW.ClaimedCostUsd IS NOT OLD.ClaimedCostUsd OR OLD.StateCode<>1 OR NEW.StateCode<>2 OR NEW.Revision<>OLD.Revision+1
BEGIN SELECT RAISE(ABORT,'immutable work bid'); END;
