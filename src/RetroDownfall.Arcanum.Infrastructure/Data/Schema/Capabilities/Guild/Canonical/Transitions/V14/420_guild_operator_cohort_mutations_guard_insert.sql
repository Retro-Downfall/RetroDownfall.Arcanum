CREATE TRIGGER IF NOT EXISTS guild_operator_cohort_mutations_guard_insert
BEFORE INSERT ON guild_operator_cohort_mutations
WHEN NOT EXISTS (
    SELECT 1 FROM guild_cohorts c JOIN guild_signets s ON s.QuestId=c.IssuerQuestId
    WHERE c.Id=NEW.CohortId AND s.InstallationGeneration=NEW.InstallationGeneration AND s.OriginPrincipalId=NEW.PrincipalId
        AND ((NEW.ActionCode=1 AND NEW.ParentId=c.IssuerQuestId)
            OR (NEW.ActionCode IN(2,3,4,5) AND NEW.ParentId=c.Id))
        AND (NEW.ActionCode<>3 OR EXISTS (
            SELECT 1 FROM guild_cohort_members m JOIN guild_cohort_member_quests a ON a.MemberId=m.Id
            WHERE m.CohortId=c.Id AND a.QuestId=NEW.ResultObjectId AND a.AssociationKindCode=1 AND m.PrimaryVotingQuestId=a.QuestId))
        AND (NEW.ActionCode<>4 OR EXISTS (
            SELECT 1 FROM guild_cohort_members m WHERE m.CohortId=c.Id AND m.Id=NEW.ResultObjectId AND m.StateCode=2))
)
BEGIN
    SELECT RAISE(ABORT, 'Guild operator Cohort receipt requires matching source and actor.');
END;
