CREATE TRIGGER IF NOT EXISTS guild_cohort_members_guard_update BEFORE UPDATE ON guild_cohort_members
WHEN NEW.Id<>OLD.Id OR NEW.CohortId<>OLD.CohortId OR NEW.AgentId<>OLD.AgentId
 OR NEW.PrimaryVotingQuestId<>OLD.PrimaryVotingQuestId OR NEW.AdmissionSequence<>OLD.AdmissionSequence
 OR OLD.StateCode<>1 OR NEW.StateCode<>2 OR NEW.RemovalSequence IS NULL
BEGIN SELECT RAISE(ABORT,'immutable cohort member'); END;
