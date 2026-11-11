CREATE TRIGGER IF NOT EXISTS guild_cohort_member_quests_guard_update BEFORE UPDATE ON guild_cohort_member_quests
WHEN NEW.MemberId<>OLD.MemberId OR NEW.QuestId<>OLD.QuestId OR NEW.AssociationKindCode<>OLD.AssociationKindCode
 OR NEW.AssociationSequence<>OLD.AssociationSequence OR OLD.Active<>1 OR NEW.Active<>0
BEGIN SELECT RAISE(ABORT,'immutable cohort association'); END;
