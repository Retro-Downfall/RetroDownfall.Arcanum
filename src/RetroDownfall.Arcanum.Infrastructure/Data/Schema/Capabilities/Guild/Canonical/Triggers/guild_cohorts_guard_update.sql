CREATE TRIGGER IF NOT EXISTS guild_cohorts_guard_update BEFORE UPDATE ON guild_cohorts
WHEN NEW.Id<>OLD.Id OR NEW.RootQuestId<>OLD.RootQuestId OR NEW.IssuerQuestId<>OLD.IssuerQuestId
 OR NEW.CampaignId IS NOT OLD.CampaignId OR NEW.PolicyCode<>OLD.PolicyCode
 OR NOT ((NEW.Name IS OLD.Name AND NEW.Retired=OLD.Retired)
     OR (OLD.Retired=0 AND NEW.Retired=1 AND NEW.Name IS NULL AND OLD.StateCode IN(4,5,6)
         AND EXISTS(SELECT 1 FROM guild_quests q WHERE q.Id=OLD.IssuerQuestId AND q.Retired=1 AND q.CancellationRequested=1)))
 OR NEW.Quorum IS NOT OLD.Quorum OR NEW.MimeType<>OLD.MimeType OR NEW.GrantJson<>OLD.GrantJson
 OR NEW.AllocationAccountId<>OLD.AllocationAccountId OR NEW.Revision<>OLD.Revision+1
 OR NEW.MembershipVersion<OLD.MembershipVersion OR NEW.ResultSequence<OLD.ResultSequence
 OR NEW.OfferSequence<OLD.OfferSequence OR NEW.NoteSequence<OLD.NoteSequence
 OR (OLD.FrozenMemberCount IS NOT NULL AND NEW.FrozenMemberCount IS NOT OLD.FrozenMemberCount)
 OR (OLD.ClosedMembershipVersion IS NOT NULL AND NEW.ClosedMembershipVersion IS NOT OLD.ClosedMembershipVersion)
 OR (OLD.DecisionCode IS NOT NULL AND NEW.DecisionCode IS NOT OLD.DecisionCode)
 OR (NEW.StateCode<>OLD.StateCode AND NOT ((OLD.StateCode=1 AND NEW.StateCode IN(2,3))
     OR (OLD.StateCode=2 AND NEW.StateCode=3) OR (OLD.StateCode=3 AND NEW.StateCode IN(4,5,6))))
BEGIN SELECT RAISE(ABORT,'immutable cohort evidence'); END;
