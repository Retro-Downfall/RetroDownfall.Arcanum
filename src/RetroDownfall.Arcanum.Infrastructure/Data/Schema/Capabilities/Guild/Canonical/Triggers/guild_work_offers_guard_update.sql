CREATE TRIGGER IF NOT EXISTS guild_work_offers_guard_update BEFORE UPDATE ON guild_work_offers
WHEN NEW.Id IS NOT OLD.Id OR NEW.CohortId IS NOT OLD.CohortId OR NEW.IssuerQuestId IS NOT OLD.IssuerQuestId
 OR NEW.OfferSequence IS NOT OLD.OfferSequence OR NEW.SkillId IS NOT OLD.SkillId OR NEW.GoalContentId IS NOT OLD.GoalContentId
 OR NEW.RequestedGrantJson IS NOT OLD.RequestedGrantJson OR NEW.BudgetHint IS NOT OLD.BudgetHint OR NEW.MimeType IS NOT OLD.MimeType
 OR NEW.BidSequence<OLD.BidSequence OR NEW.BidSequence>OLD.BidSequence+1
 OR (NEW.BidSequence<>OLD.BidSequence AND OLD.StateCode<>1)
 OR (NEW.StateCode=OLD.StateCode AND (NEW.Revision<>OLD.Revision OR NEW.AwardId IS NOT OLD.AwardId OR NEW.QuestId IS NOT OLD.QuestId OR NEW.ClosedReason IS NOT OLD.ClosedReason))
 OR (NEW.StateCode<>OLD.StateCode AND (OLD.StateCode<>1 OR NEW.StateCode NOT IN (2,3,4) OR NEW.Revision<>OLD.Revision+1))
BEGIN SELECT RAISE(ABORT,'immutable work offer'); END;
